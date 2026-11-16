using System.Data;
using System.Data.Common;
using System.Text.Json;

using BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Npgsql;
using NpgsqlTypes;

namespace MapReadModel.Core;

/// <summary>
/// Чтение карточки фиксации
///): итоговый статус, состояние по каждому привязанному источнику, последние отметки
/// с текстом и ссылка на дисклеймер.
/// </summary>
/// <remarks>
/// Модуль только читает: данные берутся сырыми SQL-запросами из <c>map_objects</c>,
/// <c>map_object_sources</c>, <c>sources</c>, <c>source_policies</c>, <c>source_statuses</c>,
/// <c>observations</c>, <c>raw_messages</c> и <c>current_statuses</c>. Ссылок на
/// <c>*.Core</c> других модулей нет. Автор отметки наружу не попадает: ни
/// <c>reporter_id</c>, ни <c>raw_messages.author</c> не выбираются, текст — <c>text</c>
/// источника или <c>comment</c> пользователя.
/// </remarks>
public sealed class ObjectCardQuery(MayakDbContext db, TimeProvider time)
{
    /// <summary>Сколько последних отметок с текстом возвращает карточка.</summary>
    public const int RecentObservationsLimit = 20;

    /// <summary>Ссылка на дисклеймер о достоверности в <c>attributes</c> карточки.</summary>
    public const string DisclaimerPath = "/disclaimer";

    /// <summary>
    /// Карточка активной фиксации; <c>null</c>, если фиксации нет или она неактивна (404).
    /// </summary>
    /// <param name="mapObjectId">Идентификатор фиксации.</param>
    /// <param name="ct">Токен отмены.</param>
    public async Task<MapObjectCard?> GetAsync(Guid mapObjectId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var header = await ReadHeaderAsync(mapObjectId, ct);

        if (header is null)
        {
            return null;
        }

        var statusesBySource = await ReadStatusesAsync(mapObjectId, ct);
        var latestBySource = await ReadLatestActualAsync(mapObjectId, now, ct);
        var countsBySource = await ReadActualCountsAsync(mapObjectId, now, ct);
        var sources = await ReadSourcesAsync(mapObjectId, ct);

        var sourceCards = sources
            .Select(source => new SourceCardView(
                source.Id,
                source.Name,
                source.SourceType,
                source.TimeoutSeconds,
                statusesBySource.TryGetValue(source.Id, out var statuses)
                    ? statuses
                    : [],
                latestBySource.TryGetValue(source.Id, out var last) ? last : null,
                countsBySource.TryGetValue(source.Id, out var count) ? count : 0))
            .ToArray();

        var recent = await ReadRecentObservationsAsync(mapObjectId, ct);

        return new MapObjectCard(
            header.Id,
            header.Name,
            header.CoverageKind,
            header.Geometry,
            header.CategoryId,
            header.ObjectTypeId,
            header.GroupId,
            header.Address,
            header.Status,
            WithDisclaimer(header.Attributes),
            sourceCards,
            recent);
    }

    private async Task<CardHeader?> ReadHeaderAsync(Guid mapObjectId, CancellationToken ct)
    {
        const string Sql = """
            select mo.id,
                   mo.name,
                   mo.coverage_kind,
                   ST_AsGeoJSON(mo.geometry::geometry, 6),
                   ot.category_id,
                   mo.object_type_id,
                   mo.group_id,
                   mo.address,
                   mo.attributes::text,
                   cs.source_status_id,
                   ss.code,
                   ss.name,
                   ss.color,
                   ss.icon,
                   cs.freshness,
                   cs.observed_at,
                   cs.source_type,
                   coalesce(cs.observation_count, 0)
            from map_objects mo
            left join object_types ot on ot.id = mo.object_type_id
            left join current_statuses cs on cs.map_object_id = mo.id
            left join source_statuses ss on ss.id = cs.source_status_id
            where mo.id = @map_object_id
              and mo.is_active
            """;

        await using var command = await CreateCommandAsync(Sql, ct);
        AddParameter(command, "map_object_id", NpgsqlDbType.Uuid, mapObjectId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new CardHeader(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            ParseJson(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetGuid(4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5),
            reader.IsDBNull(6) ? null : reader.GetGuid(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            ParseAttributes(reader.IsDBNull(8) ? null : reader.GetString(8)),
            ReadStatus(
                reader,
                statusOrdinal: 9,
                codeOrdinal: 10,
                nameOrdinal: 11,
                colorOrdinal: 12,
                iconOrdinal: 13,
                freshnessOrdinal: 14,
                observedAtOrdinal: 15,
                sourceTypeOrdinal: 16,
                countOrdinal: 17));
    }

    private async Task<IReadOnlyList<SourceRow>> ReadSourcesAsync(
        Guid mapObjectId,
        CancellationToken ct)
    {
        const string Sql = """
            select s.id,
                   s.name,
                   s.source_type,
                   coalesce(extract(epoch from p.timeout)::int, 0)
            from sources s
            left join source_policies p on p.source_id = s.id
            where s.id in (
                select source_id from map_object_sources where map_object_id = @map_object_id
                union
                select site.id
                from sources site
                join object_types ot on ot.category_id = site.category_id
                join map_objects mo on mo.object_type_id = ot.id
                where mo.id = @map_object_id
                  and site.source_type = 'site'
                  and site.is_enabled
            )
            order by (s.source_type = 'site') desc, s.name, s.id
            """;

        await using var command = await CreateCommandAsync(Sql, ct);
        AddParameter(command, "map_object_id", NpgsqlDbType.Uuid, mapObjectId);

        var rows = new List<SourceRow>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new SourceRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3)));
        }

        return rows;
    }

    private async Task<Dictionary<Guid, IReadOnlyList<CardStatusView>>> ReadStatusesAsync(
        Guid mapObjectId,
        CancellationToken ct)
    {
        const string Sql = """
            select ss.source_id, ss.id, ss.code, ss.name, ss.color, ss.icon
            from source_statuses ss
            where ss.source_id in (
                select source_id from map_object_sources where map_object_id = @map_object_id
                union
                select site.id
                from sources site
                join object_types ot on ot.category_id = site.category_id
                join map_objects mo on mo.object_type_id = ot.id
                where mo.id = @map_object_id
                  and site.source_type = 'site'
                  and site.is_enabled
            )
            order by ss.source_id, ss.sort_order, ss.name
            """;

        await using var command = await CreateCommandAsync(Sql, ct);
        AddParameter(command, "map_object_id", NpgsqlDbType.Uuid, mapObjectId);

        var result = new Dictionary<Guid, List<CardStatusView>>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sourceId = reader.GetGuid(0);

            if (!result.TryGetValue(sourceId, out var statuses))
            {
                statuses = [];
                result[sourceId] = statuses;
            }

            statuses.Add(new CardStatusView(
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<CardStatusView>)pair.Value);
    }

    /// <summary>
    /// Последнее актуальное наблюдение по каждому источнику: неотозванное и неистёкшее
    /// (<c>expires_at &gt; now</c>), порядок — <c>observed_at</c>, затем <c>received_at</c>
    /// (то же правило, что и у <c>StatusResolver</c>).
    /// </summary>
    private async Task<Dictionary<Guid, CardObservationView>> ReadLatestActualAsync(
        Guid mapObjectId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        const string Sql = """
            select distinct on (o.source_id)
                   o.source_id,
                   o.id,
                   o.source_status_id,
                   ss.code,
                   ss.name,
                   ss.color,
                   ss.icon,
                   o.observed_at,
                   o.expires_at,
                   o.source_type,
                   s.name,
                   case when o.source_type in ('telegram', 'max') then rm.text else o.comment end,
                   o.confidence,
                   o.geo_distance_m
            from observations o
            join source_statuses ss on ss.id = o.source_status_id and ss.source_id = o.source_id
            join sources s on s.id = o.source_id
            left join raw_messages rm on rm.id = o.raw_message_id
            where o.map_object_id = @map_object_id
              and not o.is_retracted
              and o.expires_at > @now
            order by o.source_id, o.observed_at desc, o.received_at desc, o.id desc
            """;

        await using var command = await CreateCommandAsync(Sql, ct);
        AddParameter(command, "map_object_id", NpgsqlDbType.Uuid, mapObjectId);
        AddParameter(command, "now", NpgsqlDbType.TimestampTz, now);

        var result = new Dictionary<Guid, CardObservationView>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetGuid(0)] = ReadObservation(reader, sourceOrdinal: 0);
        }

        return result;
    }

    private async Task<Dictionary<Guid, int>> ReadActualCountsAsync(
        Guid mapObjectId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        const string Sql = """
            select o.source_id, count(*)
            from observations o
            where o.map_object_id = @map_object_id
              and not o.is_retracted
              and o.expires_at > @now
            group by o.source_id
            """;

        await using var command = await CreateCommandAsync(Sql, ct);
        AddParameter(command, "map_object_id", NpgsqlDbType.Uuid, mapObjectId);
        AddParameter(command, "now", NpgsqlDbType.TimestampTz, now);

        var result = new Dictionary<Guid, int>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetGuid(0)] = (int)reader.GetInt64(1);
        }

        return result;
    }

    /// <summary>Последние <see cref="RecentObservationsLimit"/> отметок; отозванные не показываются.</summary>
    private async Task<IReadOnlyList<CardObservationView>> ReadRecentObservationsAsync(
        Guid mapObjectId,
        CancellationToken ct)
    {
        const string Sql = """
            select o.source_id,
                   o.id,
                   o.source_status_id,
                   ss.code,
                   ss.name,
                   ss.color,
                   ss.icon,
                   o.observed_at,
                   o.expires_at,
                   o.source_type,
                   s.name,
                   case when o.source_type in ('telegram', 'max') then rm.text else o.comment end,
                   o.confidence,
                   o.geo_distance_m
            from observations o
            join source_statuses ss on ss.id = o.source_status_id and ss.source_id = o.source_id
            join sources s on s.id = o.source_id
            left join raw_messages rm on rm.id = o.raw_message_id
            where o.map_object_id = @map_object_id
              and not o.is_retracted
            order by o.observed_at desc, o.received_at desc, o.id desc
            limit @limit
            """;

        await using var command = await CreateCommandAsync(Sql, ct);
        AddParameter(command, "map_object_id", NpgsqlDbType.Uuid, mapObjectId);
        AddParameter(command, "limit", NpgsqlDbType.Integer, RecentObservationsLimit);

        var rows = new List<CardObservationView>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(ReadObservation(reader, sourceOrdinal: 0));
        }

        return rows;
    }

    /// <summary>
    /// Читает наблюдение из общей для двух запросов формы колонок. <paramref name="sourceOrdinal"/>
    /// — позиция <c>source_id</c>: в карточке источника он первый, в списке отметок в ответ не входит.
    /// </summary>
    private static CardObservationView ReadObservation(DbDataReader reader, int sourceOrdinal)
    {
        var offset = sourceOrdinal + 1;

        return new CardObservationView(
            reader.GetGuid(offset),
            new CardStatusView(
                reader.GetGuid(offset + 1),
                reader.GetString(offset + 2),
                reader.GetString(offset + 3),
                reader.GetString(offset + 4),
                reader.GetString(offset + 5)),
            ReadTimestamp(reader, offset + 6),
            ReadTimestamp(reader, offset + 7),
            reader.GetString(offset + 8),
            reader.IsDBNull(offset + 9) ? null : reader.GetString(offset + 9),
            reader.IsDBNull(offset + 10) ? null : reader.GetString(offset + 10),
            reader.IsDBNull(offset + 11) ? null : reader.GetFloat(offset + 11),
            reader.IsDBNull(offset + 12) ? null : reader.GetInt32(offset + 12));
    }

    private static MapStatusView? ReadStatus(
        DbDataReader reader,
        int statusOrdinal,
        int codeOrdinal,
        int nameOrdinal,
        int colorOrdinal,
        int iconOrdinal,
        int freshnessOrdinal,
        int observedAtOrdinal,
        int sourceTypeOrdinal,
        int countOrdinal)
    {
        // Проекция с source_status_id = null означает «неизвестно»: в контракте это status: null.
        if (reader.IsDBNull(statusOrdinal)
            || reader.IsDBNull(codeOrdinal)
            || reader.IsDBNull(nameOrdinal)
            || reader.IsDBNull(colorOrdinal)
            || reader.IsDBNull(iconOrdinal)
            || reader.IsDBNull(freshnessOrdinal)
            || reader.IsDBNull(observedAtOrdinal)
            || reader.IsDBNull(sourceTypeOrdinal))
        {
            return null;
        }

        return new MapStatusView(
            reader.GetGuid(statusOrdinal),
            reader.GetString(codeOrdinal),
            reader.GetString(nameOrdinal),
            reader.GetString(colorOrdinal),
            reader.GetString(iconOrdinal),
            reader.GetString(freshnessOrdinal),
            ReadTimestamp(reader, observedAtOrdinal),
            reader.GetString(sourceTypeOrdinal),
            reader.GetInt32(countOrdinal));
    }

    /// <summary>
    /// Атрибуты фиксации плюс ссылка на дисклеймер. Контракт описывает
    /// <c>attributes</c> как свободный объект, поэтому ссылка не ломает схему.
    /// </summary>
    private static IReadOnlyDictionary<string, JsonElement> WithDisclaimer(
        IReadOnlyDictionary<string, JsonElement> attributes)
    {
        var result = new Dictionary<string, JsonElement>(attributes, StringComparer.Ordinal)
        {
            ["disclaimerUrl"] = JsonSerializer.SerializeToElement(DisclaimerPath),
        };

        return result;
    }

    private static IReadOnlyDictionary<string, JsonElement> ParseAttributes(string? json)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            result[property.Name] = property.Value.Clone();
        }

        return result;
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }

    private async Task<DbCommand> CreateCommandAsync(string sql, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

        return command;
    }

    private static void AddParameter(DbCommand command, string name, NpgsqlDbType type, object value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });

    private static DateTimeOffset ReadTimestamp(DbDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetFieldValue<DateTime>(ordinal), DateTimeKind.Utc));

    private sealed record CardHeader(
        Guid Id,
        string Name,
        string CoverageKind,
        JsonElement Geometry,
        Guid? CategoryId,
        Guid? ObjectTypeId,
        Guid? GroupId,
        string? Address,
        IReadOnlyDictionary<string, JsonElement> Attributes,
        MapStatusView? Status);

    private sealed record SourceRow(Guid Id, string Name, string SourceType, int TimeoutSeconds);
}

/// <summary>
/// Карточка фиксации (OpenAPI <c>MapObjectCard</c>): поля <c>MapFeature</c>, <c>attributes</c>
/// со ссылкой на дисклеймер, источники и последние отметки с текстом.
/// </summary>
/// <param name="Id">Идентификатор фиксации.</param>
/// <param name="Name">Название.</param>
/// <param name="CoverageKind"><c>point</c> или <c>polygon</c>.</param>
/// <param name="Geometry">GeoJSON геометрии.</param>
/// <param name="CategoryId">Категория (через тип объекта).</param>
/// <param name="ObjectTypeId">Тип объекта.</param>
/// <param name="GroupId">Группа.</param>
/// <param name="Address">Адрес.</param>
/// <param name="Status">Итоговый статус из <c>current_statuses</c>; <c>null</c> — «неизвестно».</param>
/// <param name="Attributes">Атрибуты фиксации и ссылка на дисклеймер (<c>disclaimerUrl</c>).</param>
/// <param name="Sources">Состояние по каждому привязанному источнику.</param>
/// <param name="Observations">Последние отметки с текстом, без автора.</param>
public sealed record MapObjectCard(
    Guid Id,
    string Name,
    string CoverageKind,
    JsonElement Geometry,
    Guid? CategoryId,
    Guid? ObjectTypeId,
    Guid? GroupId,
    string? Address,
    MapStatusView? Status,
    IReadOnlyDictionary<string, JsonElement> Attributes,
    IReadOnlyList<SourceCardView> Sources,
    IReadOnlyList<CardObservationView> Observations);

/// <summary>Состояние источника в карточке (OpenAPI <c>SourceCardView</c>).</summary>
/// <param name="Id">Идентификатор источника.</param>
/// <param name="Name">Название источника.</param>
/// <param name="SourceType">Тип адаптера: <c>telegram_chat</c> или <c>max_chat</c>.</param>
/// <param name="TimeoutSeconds">Таймаут актуальности источника.</param>
/// <param name="Statuses">Статусы источника для выбора в отметке.</param>
/// <param name="LastObservation">
/// Последнее актуальное наблюдение источника; <c>null</c> — актуальных нет (источник
/// показывается как «неизвестно»).
/// </param>
/// <param name="ObservationCount">Число актуальных наблюдений источника.</param>
public sealed record SourceCardView(
    Guid Id,
    string Name,
    string SourceType,
    int TimeoutSeconds,
    IReadOnlyList<CardStatusView> Statuses,
    CardObservationView? LastObservation,
    int ObservationCount);

/// <summary>Публичные поля статуса источника (OpenAPI <c>SourceStatusPublic</c>).</summary>
/// <param name="Id">Идентификатор статуса.</param>
/// <param name="Code">Код статуса.</param>
/// <param name="Name">Название.</param>
/// <param name="Color">Ключ палитры.</param>
/// <param name="Icon">Ключ иконки.</param>
public sealed record CardStatusView(Guid Id, string Code, string Name, string Color, string Icon);

/// <summary>
/// Отметка в карточке без автора (OpenAPI <c>PublicObservation</c>): статус, время,
/// достоверность и текст-основание.
/// </summary>
/// <param name="Id">Идентификатор наблюдения.</param>
/// <param name="Status">Статус, выставленный отметкой.</param>
/// <param name="ObservedAt">Время наблюдения.</param>
/// <param name="ExpiresAt">Момент истечения актуальности.</param>
/// <param name="SourceType"><c>manual</c>, <c>moderator</c>, <c>telegram</c> или <c>max</c>.</param>
/// <param name="SourceName">Название источника.</param>
/// <param name="Text">Сообщение чата или комментарий пользователя.</param>
/// <param name="Confidence">Достоверность 0..1.</param>
/// <param name="GeoDistanceM">Расстояние от пользователя до фиксации, метры.</param>
public sealed record CardObservationView(
    Guid Id,
    CardStatusView Status,
    DateTimeOffset ObservedAt,
    DateTimeOffset ExpiresAt,
    string SourceType,
    string? SourceName,
    string? Text,
    float? Confidence,
    int? GeoDistanceM);
