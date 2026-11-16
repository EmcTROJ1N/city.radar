using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using BuildingBlocks.Persistence;

namespace IngestionWorker.Host;

/// <summary>
/// Чтение конфигурации всех включённых источников для горячего применения настроек
///.
/// </summary>
/// <remarks>
/// Один опрос — это три запроса: источники с политикой и метаданными секретов, все статусы
/// источников и все привязки к фиксациям. Хеш конфигурации считается по полям, которые влияют
/// на адаптер и промпт: тип, настройки чата, таймаут, порог, префильтр, промпт, режим
/// «по тексту», статусы, привязанные фиксации, сессия и прокси с временем изменения, включённость.
/// </remarks>
internal sealed class SourceConfigReader(MayakDbContext db)
{
    private const string SourcesSql =
        """
        select s.id,
               s.source_type,
               s.settings::text,
               s.user_prompt,
               s.link_by_text,
               p.timeout,
               p.confidence_threshold,
               p.prefilter::text,
               s.telegram_session_id,
               session.updated_at,
               s.proxy_id,
               source_proxy.updated_at,
               (global_proxy.value #>> '{}')::uuid,
               global_proxy_secret.updated_at
        from sources s
        join source_policies p on p.source_id = s.id
        left join secrets session on session.id = s.telegram_session_id
        left join secrets source_proxy on source_proxy.id = s.proxy_id
        left join global_settings global_proxy
            on global_proxy.key = 'proxy_secret_id'
           and jsonb_typeof(global_proxy.value) = 'string'
        left join secrets global_proxy_secret on global_proxy_secret.id = (global_proxy.value #>> '{}')::uuid
        where s.is_enabled
          and s.source_type <> 'site'
        order by s.id
        """;

    private const string StatusesSql =
        """
        select source_id, code, name, description, sort_order
        from source_statuses
        order by source_id, sort_order, name, id
        """;

    private const string ObjectsSql =
        """
        select links.source_id, links.map_object_id
        from map_object_sources links
        order by links.source_id, links.map_object_id
        """;

    private readonly MayakDbContext _db = db;

    /// <summary>Возвращает снимки всех включённых источников с хешем конфигурации.</summary>
    public async Task<IReadOnlyList<SourceConfigSnapshot>> ReadAsync(CancellationToken ct)
    {
        var sources = await ReadSourcesAsync(ct).ConfigureAwait(false);
        if (sources.Count == 0)
        {
            return [];
        }

        var statuses = await ReadStatusesAsync(ct).ConfigureAwait(false);
        var objects = await ReadObjectsAsync(ct).ConfigureAwait(false);

        var result = new List<SourceConfigSnapshot>(sources.Count);
        foreach (var source in sources)
        {
            var fingerprint = BuildFingerprint(
                source,
                statuses.GetValueOrDefault(source.Id, []),
                objects.GetValueOrDefault(source.Id, []));

            result.Add(new SourceConfigSnapshot(
                source.Id,
                fingerprint,
                source.SourceType,
                ParseJson(source.SettingsJson)));
        }

        return result;
    }

    private async Task<List<SourceRow>> ReadSourcesAsync(CancellationToken ct)
    {
        await using var command = await WorkerSql.CreateCommandAsync(_db, SourcesSql, ct)
            .ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var rows = new List<SourceRow>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new SourceRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetBoolean(4),
                reader.GetFieldValue<TimeSpan>(5),
                reader.IsDBNull(6) ? null : reader.GetFloat(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetGuid(8),
                reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
                reader.IsDBNull(10) ? null : reader.GetGuid(10),
                reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
                reader.IsDBNull(12) ? null : reader.GetGuid(12),
                reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13)));
        }

        return rows;
    }

    private async Task<Dictionary<Guid, List<StatusRow>>> ReadStatusesAsync(CancellationToken ct)
    {
        await using var command = await WorkerSql.CreateCommandAsync(_db, StatusesSql, ct)
            .ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var rows = new Dictionary<Guid, List<StatusRow>>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var sourceId = reader.GetGuid(0);
            if (!rows.TryGetValue(sourceId, out var list))
            {
                list = [];
                rows[sourceId] = list;
            }

            list.Add(new StatusRow(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4)));
        }

        return rows;
    }

    private async Task<Dictionary<Guid, List<ObjectRow>>> ReadObjectsAsync(CancellationToken ct)
    {
        await using var command = await WorkerSql.CreateCommandAsync(_db, ObjectsSql, ct)
            .ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var rows = new Dictionary<Guid, List<ObjectRow>>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var sourceId = reader.GetGuid(0);
            if (!rows.TryGetValue(sourceId, out var list))
            {
                list = [];
                rows[sourceId] = list;
            }

            list.Add(new ObjectRow(reader.GetGuid(1)));
        }

        return rows;
    }

    /// <summary>
    /// Считает SHA-256 по каноническому описанию конфигурации. Каждое поле пишется с длиной,
    /// чтобы разные комбинации не давали одинаковую строку; порядок статусов и фиксаций
    /// задан запросами и стабилен.
    /// </summary>
    private static string BuildFingerprint(
        SourceRow source,
        IReadOnlyList<StatusRow> statuses,
        IReadOnlyList<ObjectRow> objects)
    {
        var builder = new StringBuilder();

        AppendField(builder, source.Id.ToString("D", CultureInfo.InvariantCulture));
        AppendField(builder, source.SourceType);
        AppendField(builder, source.SettingsJson);
        AppendField(builder, source.UserPrompt);
        AppendField(builder, source.LinkByText ? "1" : "0");
        AppendField(builder, source.Timeout.Ticks.ToString(CultureInfo.InvariantCulture));
        AppendField(builder, source.ConfidenceThreshold?.ToString("R", CultureInfo.InvariantCulture));
        AppendField(builder, source.PrefilterJson);
        AppendField(builder, source.SessionSecretId?.ToString("D", CultureInfo.InvariantCulture));
        AppendField(builder, Ticks(source.SessionUpdatedAt));
        AppendField(builder, source.ProxySecretId?.ToString("D", CultureInfo.InvariantCulture));
        AppendField(builder, Ticks(source.ProxyUpdatedAt));
        AppendField(builder, source.GlobalProxySecretId?.ToString("D", CultureInfo.InvariantCulture));
        AppendField(builder, Ticks(source.GlobalProxyUpdatedAt));
        AppendField(builder, "enabled");

        AppendField(builder, statuses.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var status in statuses)
        {
            AppendField(builder, status.Code);
            AppendField(builder, status.Name);
            AppendField(builder, status.Description);
            AppendField(builder, status.SortOrder.ToString(CultureInfo.InvariantCulture));
        }

        AppendField(builder, objects.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var linkedObject in objects)
        {
            // Название и адрес в хеш не входят: промпт собирается заново на каждое сообщение,
            // поэтому переименование фиксации не требует перезапуска адаптера.
            AppendField(builder, linkedObject.Id.ToString("D", CultureInfo.InvariantCulture));
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));

        return Convert.ToHexString(hash);
    }

    private static string Ticks(DateTimeOffset? value) =>
        value?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static void AppendField(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-1:").Append('\n');

            return;
        }

        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('\n');
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }

    private sealed record SourceRow(
        Guid Id,
        string SourceType,
        string SettingsJson,
        string UserPrompt,
        bool LinkByText,
        TimeSpan Timeout,
        float? ConfidenceThreshold,
        string? PrefilterJson,
        Guid? SessionSecretId,
        DateTimeOffset? SessionUpdatedAt,
        Guid? ProxySecretId,
        DateTimeOffset? ProxyUpdatedAt,
        Guid? GlobalProxySecretId,
        DateTimeOffset? GlobalProxyUpdatedAt);

    private sealed record StatusRow(string Code, string Name, string Description, int SortOrder);

    private sealed record ObjectRow(Guid Id);
}
