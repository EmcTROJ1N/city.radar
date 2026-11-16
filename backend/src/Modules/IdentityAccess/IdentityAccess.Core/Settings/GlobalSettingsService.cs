using System.Text.Json;

using BuildingBlocks.Persistence;

using IdentityAccess.Contracts;
using IdentityAccess.Core.Domain;

using Microsoft.EntityFrameworkCore;

namespace IdentityAccess.Core.Settings;

/// <summary>
/// Чтение глобальных настроек из <c>global_settings</c>. Ключи, которых нет в БД,
/// принимают значения по умолчанию из <see cref="GlobalSettingsRegistry"/>; повреждённое
/// (не JSON или не по схеме) значение тоже не роняет чтение — действует значение по умолчанию,
/// а исправить его можно через <c>PUT /admin/settings</c>.
/// </summary>
public sealed class GlobalSettingsService(MayakDbContext db) : IGlobalSettings
{
    public async Task<GlobalSettingsSnapshot> GetAsync(CancellationToken ct)
    {
        var rows = await db.Set<GlobalSetting>()
            .AsNoTracking()
            .ToListAsync(ct);

        var stored = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var json = Parse(row.Value);
            if (json is { } value)
            {
                stored[row.Key] = value;
            }
        }

        var snapshot = GlobalSettingsRegistry.Defaults;
        foreach (var definition in GlobalSettingsRegistry.All)
        {
            if (stored.TryGetValue(definition.Key, out var json)
                && definition.TryParse(json, out var value, out _))
            {
                snapshot = definition.Apply(snapshot, value);
            }
        }

        return snapshot;
    }

    private static JsonElement? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
