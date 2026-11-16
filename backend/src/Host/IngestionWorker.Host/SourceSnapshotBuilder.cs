using System.Text.Json;

using BuildingBlocks.Persistence;

using Catalog.Contracts;

using IdentityAccess.Contracts;

using Ingestion.Pipeline;

using Sources.Contracts;

namespace IngestionWorker.Host;

/// <summary>
/// Сборка <see cref="SourceRuntimeSnapshot"/> из БД на каждое сообщение:
/// промпт, статусы, привязанные фиксации, порог и префильтр читаются заново, поэтому изменение
/// настроек источника применяется к следующему сообщению без перезапуска Worker-а.
/// </summary>
internal sealed class SourceSnapshotBuilder(
    MayakDbContext db,
    ISourceDirectory directory,
    IMapObjectDirectory mapObjects,
    IGlobalSettings globalSettings)
{
    private const string SourceSql =
        """
        select source.user_prompt, policy.prefilter::text
        from sources source
        left join source_policies policy on policy.source_id = source.id
        where source.id = @source_id
        """;

    /// <summary>
    /// Возвращает снимок настроек источника либо <c>null</c>, если источник удалён к моменту
    /// обработки сообщения.
    /// </summary>
    public async Task<SourceRuntimeSnapshot?> BuildAsync(Guid sourceId, CancellationToken ct)
    {
        var policy = await directory.GetPolicyAsync(sourceId, ct).ConfigureAwait(false);
        if (policy is null)
        {
            return null;
        }

        var source = await ReadSourceAsync(sourceId, ct).ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        var statuses = await directory.GetStatusesAsync(sourceId, ct).ConfigureAwait(false);
        var linkedObjects = await mapObjects.GetObjectsOfSourceAsync(sourceId, ct).ConfigureAwait(false);

        var threshold = policy.ConfidenceThreshold is { } sourceThreshold
            ? sourceThreshold
            : (await globalSettings.GetAsync(ct).ConfigureAwait(false)).ConfidenceThresholdDefault;

        return new SourceRuntimeSnapshot(
            sourceId,
            statuses,
            source.Value.UserPrompt,
            policy.LinkByText,
            linkedObjects,
            threshold,
            source.Value.Prefilter);
    }

    private async Task<(string UserPrompt, PrefilterConfig? Prefilter)?> ReadSourceAsync(
        Guid sourceId,
        CancellationToken ct)
    {
        await using var command = await WorkerSql.CreateCommandAsync(db, SourceSql, ct)
            .ConfigureAwait(false);
        WorkerSql.AddParameter(command, "source_id", sourceId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var prompt = reader.GetString(0);
        var prefilter = reader.IsDBNull(1) ? null : ParsePrefilter(reader.GetString(1));

        return (prompt, prefilter);
    }

    /// <summary>
    /// Разбирает <c>source_policies.prefilter</c> в <see cref="PrefilterConfig"/>. Структура
    /// свободная (jsonb), поэтому неизвестные поля игнорируются, а невалидный JSON означает
    /// «фильтра нет»: префильтр не должен ронять обработку сообщений.
    /// </summary>
    internal static PrefilterConfig? ParsePrefilter(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var keywords = ReadStrings(document.RootElement, "keywords");
            var regexes = ReadStrings(document.RootElement, "regexes");

            return keywords is null && regexes is null
                ? null
                : new PrefilterConfig(keywords, regexes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string>? ReadStrings(JsonElement root, string name)
    {
        JsonElement value = default;
        var found = false;

        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = property.Value;
            found = true;
            break;
        }

        if (!found || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                result.Add(item.GetString() ?? string.Empty);
            }
        }

        return result.Count == 0 ? null : result;
    }
}
