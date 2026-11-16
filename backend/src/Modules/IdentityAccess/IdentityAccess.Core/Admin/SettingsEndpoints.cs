using System.Globalization;
using System.Text.Json;

using BuildingBlocks.Persistence;
using BuildingBlocks.Web;

using IdentityAccess.Contracts;
using IdentityAccess.Core.Domain;
using IdentityAccess.Core.Settings;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace IdentityAccess.Core.Admin;

/// <summary>
/// <c>GET/PUT /admin/settings</c> (схема <c>GlobalSettings</c>): чтение полного
/// снимка настроек и частичное обновление. Неизвестный ключ или значение не по схеме — 422 с
/// <c>errors[field,message]</c>; успешное изменение пишется в аудит (<c>settings.update</c>) и
/// сохраняется в <c>global_settings</c> одним <c>SaveChanges</c>. Доступ — <c>settings.global.manage</c>.
/// </summary>
public static class SettingsEndpoints
{
    private const string SettingsEntityType = "settings";

    /// <summary>Путь к настройке в <c>errors[].field</c>, если тело запроса не объект.</summary>
    private const string BodyField = "$";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Регистрирует настройки внутри текущего префикса (например, <c>/api/v1</c>).</summary>
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints
            .MapGroup("/admin/settings")
            .RequireAuthorization(Permissions.SettingsGlobalManage);

        group.MapGet(string.Empty, GetAsync);
        group.MapPut(string.Empty, UpdateAsync);

        return endpoints;
    }

    private static async Task<IResult> GetAsync(IGlobalSettings settings, CancellationToken ct) =>
        Results.Ok(await settings.GetAsync(ct));

    /// <summary>
    /// Частичное обновление: изменяются только переданные поля, отсутствующие ключи остаются
    /// значениями по умолчанию. В аудит попадают только фактически изменённые поля.
    /// </summary>
    private static async Task<IResult> UpdateAsync(
        HttpContext context,
        MayakDbContext db,
        IGlobalSettings settings,
        IAuditLog audit,
        ICurrentUser currentUser,
        TimeProvider time,
        CancellationToken ct)
    {
        using var document = await AdminRequestJson.TryReadObjectAsync(context.Request, ct);
        if (document is null)
        {
            return ProblemResponses.Validation(
                new ValidationError(BodyField, "Ожидается JSON-объект с настройками"));
        }

        var errors = new List<ValidationError>();
        var updates = new List<(SettingDefinition Definition, object? Value)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            var definition = GlobalSettingsRegistry.FindByField(property.Name);
            if (definition is null)
            {
                errors.Add(new ValidationError(property.Name, "Неизвестная настройка"));
                continue;
            }

            if (!seen.Add(definition.Field))
            {
                errors.Add(new ValidationError(property.Name, "Настройка указана более одного раза"));
                continue;
            }

            if (!definition.TryParse(property.Value, out var value, out var error))
            {
                errors.Add(new ValidationError(definition.Field, error ?? "Недопустимое значение"));
                continue;
            }

            updates.Add((definition, value));
        }

        if (errors.Count > 0)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        var before = await settings.GetAsync(ct);
        var after = before;
        var changed = new List<(SettingDefinition Definition, object? Before, object? After)>();

        foreach (var (definition, value) in updates)
        {
            var current = definition.Read(after);
            if (Equals(current, value))
            {
                continue;
            }

            changed.Add((definition, current, value));
            after = definition.Apply(after, value);
        }

        if (changed.Count == 0)
        {
            return Results.Ok(after);
        }

        var keys = changed.Select(entry => entry.Definition.Key).ToArray();
        var rows = await db.Set<GlobalSetting>()
            .Where(setting => keys.Contains(setting.Key))
            .ToListAsync(ct);

        var updatedAt = time.GetUtcNow();

        foreach (var (definition, _, value) in changed)
        {
            var row = rows.FirstOrDefault(setting =>
                string.Equals(setting.Key, definition.Key, StringComparison.Ordinal));

            if (row is null)
            {
                row = new GlobalSetting { Key = definition.Key };
                db.Add(row);
            }

            row.Value = Serialize(value);
            row.UpdatedBy = currentUser.UserId;
            row.UpdatedAt = updatedAt;
        }

        // Только изменённые поля: before — как было (с учётом значений по умолчанию), after — как стало.
        var beforeFields = changed.ToDictionary(
            entry => entry.Definition.Field,
            entry => entry.Before,
            StringComparer.Ordinal);
        var afterFields = changed.ToDictionary(
            entry => entry.Definition.Field,
            entry => entry.After,
            StringComparer.Ordinal);

        await audit.WriteAsync("settings.update", SettingsEntityType, null, beforeFields, afterFields, ct: ct);

        // Настройки и аудит сохраняются одной транзакцией SaveChanges.
        await db.SaveChangesAsync(ct);

        return Results.Ok(after);
    }

    /// <summary>jsonb-значение настройки в том же виде, в каком оно пришло в запросе.</summary>
    private static string Serialize(object? value) =>
        value is null
            ? "null"
            : value switch
            {
                int number => number.ToString(CultureInfo.InvariantCulture),
                double number => number.ToString("R", CultureInfo.InvariantCulture),
                Guid id => JsonSerializer.Serialize(id, JsonOptions),
                _ => JsonSerializer.Serialize(value, value.GetType(), JsonOptions),
            };
}
