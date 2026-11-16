using System.Text.Json;

using BuildingBlocks.Persistence;

using IdentityAccess.Contracts;
using IdentityAccess.Core.Domain;

using Microsoft.EntityFrameworkCore;

namespace IdentityAccess.Core.Seeding;

/// <summary>
/// Идемпотентный dev/test-seed пользователей из <c>db/seed_dev.sql</c>:
/// пять пользователей с фиксированными GUID, MAX-id и ролями (a1, a2 → <c>user</c>,
/// a3, a4 → <c>moderator</c>, a5 → <c>admin</c>). В продуктивном окружении не запускается.
/// </summary>
public sealed class DevSeeder(MayakDbContext db)
{
    /// <summary>GUID dev-пользователя с ролью <c>user</c> (из <c>db/seed_dev.sql</c>).</summary>
    internal static readonly Guid UserId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    /// <summary>GUID dev-пользователя с ролью <c>moderator</c>.</summary>
    internal static readonly Guid ModeratorId = Guid.Parse("00000000-0000-0000-0000-0000000000a3");

    /// <summary>GUID dev-пользователя с ролью <c>admin</c>.</summary>
    internal static readonly Guid AdminId = Guid.Parse("00000000-0000-0000-0000-0000000000a5");

    /// <summary>Тестовые пользователи из <c>db/seed_dev.sql</c>; GUID совпадают с файлом.</summary>
    private static readonly DevUser[] DevUsers =
    [
        new(UserId, 900000001, "Виктор", Roles.User),
        new(Guid.Parse("00000000-0000-0000-0000-0000000000a2"), 900000002, "Марина", Roles.User),
        new(ModeratorId, 900000003, "Ольга", Roles.Moderator),
        new(Guid.Parse("00000000-0000-0000-0000-0000000000a4"), 900000004, "Игорь", Roles.Moderator),
        new(AdminId, 900000005, "Герман", Roles.Admin),
    ];

    /// <summary>
    /// Создаёт недостающих dev-пользователей и их привязки к ролям. Повторный вызов ничего
    /// не дублирует; существующих пользователей не изменяет. Если роли пользователя
    /// меняли через админку (есть запись <c>user.roles.assign</c>), сид их не возвращает.
    /// </summary>
    /// <exception cref="InvalidOperationException">Роли ещё не созданы — сначала нужен <see cref="RoleSeeder"/>.</exception>
    public async Task SeedAsync(CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var roles = await db.Set<AppRole>().ToListAsync(ct);
        var roleIds = roles.ToDictionary(role => role.Name, role => role.Id, StringComparer.Ordinal);

        var userIds = DevUsers.Select(user => user.Id).ToArray();
        var existingUsers = await db.Set<AppUser>()
            .Where(user => userIds.Contains(user.Id))
            .ToListAsync(ct);
        var existingLinks = await db.Set<UserRole>()
            .Where(link => userIds.Contains(link.UserId))
            .ToListAsync(ct);
        var manualRoles = await LatestManualRolesAsync(userIds, ct);

        foreach (var seed in DevUsers)
        {
            if (!roleIds.TryGetValue(seed.RoleName, out var roleId))
            {
                throw new InvalidOperationException(
                    $"Нет роли '{seed.RoleName}': сначала выполните {nameof(RoleSeeder)}.");
            }

            if (existingUsers.TrueForAll(user => user.Id != seed.Id))
            {
                var user = new AppUser
                {
                    Id = seed.Id,
                    UserName = seed.MaxUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    NormalizedUserName = seed.MaxUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    MaxUserId = seed.MaxUserId,
                    DisplayName = seed.DisplayName,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                db.Add(user);
                existingUsers.Add(user);
            }

            // Роль, снятую через админку, dev-сид не возвращает.
            var keptByAdmin = !manualRoles.TryGetValue(seed.Id, out var assigned)
                || assigned.Contains(seed.RoleName);
            if (keptByAdmin
                && existingLinks.TrueForAll(link => link.UserId != seed.Id || link.RoleId != roleId))
            {
                var link = new UserRole { UserId = seed.Id, RoleId = roleId };
                db.Add(link);
                existingLinks.Add(link);
            }
        }

        await db.SaveChangesAsync(ct);

        // Остаток db/seed_dev.sql (каталог, источник со статусами и фиксация) сидируется в
        // composition root — Api.Host/Seeding/DevCatalogSeeder.cs: IdentityAccess не должен
        // писать в таблицы чужих модулей.
    }

    /// <summary>
    /// Последний ручной набор ролей каждого dev-пользователя из журнала аудита.
    /// Отсутствие записи означает «роли не трогали» — действует обычный dev-сид.
    /// </summary>
    private async Task<Dictionary<Guid, HashSet<string>>> LatestManualRolesAsync(
        Guid[] userIds,
        CancellationToken ct)
    {
        var ids = userIds.Select(id => id.ToString()).ToArray();
        var entries = await db.Set<AuditEntry>()
            .AsNoTracking()
            .Where(entry => entry.Action == "user.roles.assign"
                && entry.EntityType == "user"
                && entry.EntityId != null
                && ids.Contains(entry.EntityId))
            .OrderByDescending(entry => entry.Id)
            .Select(entry => new { entry.EntityId, entry.After })
            .ToListAsync(ct);

        var result = new Dictionary<Guid, HashSet<string>>();
        foreach (var entry in entries)
        {
            // Записи идут от новых к старым: первая на пользователя — актуальная.
            if (!Guid.TryParse(entry.EntityId, out var userId) || result.ContainsKey(userId)) continue;
            result[userId] = ParseRoles(entry.After);
        }

        return result;
    }

    /// <summary>Поле <c>roles</c> из jsonb-снимка аудита; некорректный JSON — пустой набор.</summary>
    private static HashSet<string> ParseRoles(string? json)
    {
        var roles = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return roles;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("roles", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return roles;
            }

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } role)
                {
                    roles.Add(role);
                }
            }
        }
        catch (JsonException)
        {
            // Битый снимок не должен ломать старт dev-стенда.
        }

        return roles;
    }

    private sealed record DevUser(Guid Id, long MaxUserId, string DisplayName, string RoleName);
}
