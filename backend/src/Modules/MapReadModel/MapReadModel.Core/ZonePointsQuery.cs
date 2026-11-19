using System.Data;
using System.Data.Common;

using BuildingBlocks.Persistence;

using MapReadModel.Core.Geocoding;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Npgsql;

using NpgsqlTypes;

namespace MapReadModel.Core;

/// <summary>
/// Временные точки выбранной зоны: актуальные сообщения с адресом группируются по адресу
/// (побеждает самое свежее), адрес геокодируется, точки дальше <see cref="MaxDistanceMeters"/>
/// от зоны отбрасываются. Ничего не сохраняется — точки живут, пока зона выбрана на карте.
/// </summary>
public sealed class ZonePointsQuery(MayakDbContext db, IGeocoder geocoder, TimeProvider time)
{
    /// <summary>Сколько новых запросов к геокодеру допускает один вызов; остальные адреса — на следующем опросе.</summary>
    public const int MaxNewGeocodesPerCall = 5;

    /// <summary>
    /// Сколько ответ может ждать геокодер: дальше новые адреса откладываются до следующего опроса,
    /// а уже известные отдаются из кэша. Держит ответ в секундах, даже если Nominatim завис.
    /// </summary>
    public static readonly TimeSpan GeocodingDeadline = TimeSpan.FromSeconds(4);

    /// <summary>Допуск за границей зоны: границы демо-зон приблизительны.</summary>
    public const int MaxDistanceMeters = 300;

    /// <summary>Точки зоны; <c>null</c> — фиксации нет или она неактивна, пусто — это не зона.</summary>
    /// <param name="mapObjectId">Фиксация.</param>
    /// <param name="ct">Токен отмены.</param>
    public async Task<ZonePointsResponse?> GetAsync(Guid mapObjectId, CancellationToken ct)
    {
        var zone = await ReadZoneAsync(mapObjectId, ct);
        if (zone is null)
        {
            return null;
        }

        if (zone.CoverageKind != "polygon")
        {
            return new ZonePointsResponse([]);
        }

        var messages = await ReadMessagesAsync(mapObjectId, ct);
        var groups = messages
            .GroupBy(message => AddressNormalizer.Normalize(message.Address))
            .Select(group => (Latest: group.First(), Count: group.Count()))
            .ToList();

        var budget = new GeocodeBudget(MaxNewGeocodesPerCall);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(GeocodingDeadline);

        var located = new List<(AddressMessage Message, int Count, GeoPoint Point)>();
        foreach (var (latest, count) in groups)
        {
            GeoPoint? point;
            try
            {
                point = await geocoder.GeocodeAsync(latest.Address, zone.Box, budget, deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Время на сеть вышло: остальные адреса — только из кэша, без новых запросов.
                budget = new GeocodeBudget(0);
                point = await geocoder.GeocodeAsync(latest.Address, zone.Box, budget, ct);
            }

            if (point is not null)
            {
                located.Add((latest, count, point));
            }
        }

        var near = await FilterNearZoneAsync(mapObjectId, located.Select(item => item.Point).ToList(), ct);

        return new ZonePointsResponse(
            located
                .Where((_, index) => near.Contains(index))
                .Select(item => new ZonePointView(
                    item.Message.Address,
                    new ZonePointLocation(item.Point.Lat, item.Point.Lon),
                    item.Message.Status,
                    item.Message.ObservedAt,
                    item.Message.Text,
                    item.Count))
                .ToList());
    }

    private async Task<ZoneRow?> ReadZoneAsync(Guid mapObjectId, CancellationToken ct)
    {
        const string Sql = """
            select mo.coverage_kind,
                   ST_XMin(mo.geometry::geometry), ST_YMin(mo.geometry::geometry),
                   ST_XMax(mo.geometry::geometry), ST_YMax(mo.geometry::geometry)
            from map_objects mo
            where mo.id = @map_object_id and mo.is_active
            """;

        await using var command = await CreateCommandAsync(Sql, ct);
        AddParameter(command, "map_object_id", NpgsqlDbType.Uuid, mapObjectId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new ZoneRow(
            reader.GetString(0),
            new GeoBox(reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4)));
    }

    /// <summary>Актуальные сообщения зоны с адресом, от свежих к старым.</summary>
    private async Task<List<AddressMessage>> ReadMessagesAsync(Guid mapObjectId, CancellationToken ct)
    {
        const string Sql = """
            select o.address,
                   ss.id, ss.code, ss.name, ss.color, ss.icon,
                   o.observed_at,
                   case when o.source_type in ('telegram', 'max') then rm.text else o.comment end
            from observations o
            join source_statuses ss on ss.id = o.source_status_id and ss.source_id = o.source_id
            left join raw_messages rm on rm.id = o.raw_message_id
            where o.map_object_id = @map_object_id
              and not o.is_retracted
              and o.expires_at > @now
              and o.address is not null
            order by o.observed_at desc, o.received_at desc, o.id desc
            """;

        await using var command = await CreateCommandAsync(Sql, ct);
        AddParameter(command, "map_object_id", NpgsqlDbType.Uuid, mapObjectId);
        AddParameter(command, "now", NpgsqlDbType.TimestampTz, time.GetUtcNow());

        var messages = new List<AddressMessage>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            messages.Add(new AddressMessage(
                reader.GetString(0),
                new CardStatusView(
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5)),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetFieldValue<DateTime>(6), DateTimeKind.Utc)),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return messages;
    }

    /// <summary>Индексы точек не дальше <see cref="MaxDistanceMeters"/> от зоны.</summary>
    private async Task<HashSet<int>> FilterNearZoneAsync(Guid mapObjectId, List<GeoPoint> points, CancellationToken ct)
    {
        if (points.Count == 0)
        {
            return [];
        }

        const string Sql = """
            select p.ordinality::int - 1
            from map_objects mo,
                 unnest(@lats, @lons) with ordinality as p(lat, lon, ordinality)
            where mo.id = @map_object_id
              and ST_DWithin(mo.geometry, ST_SetSRID(ST_MakePoint(p.lon, p.lat), 4326)::geography, @distance)
            """;

        await using var command = await CreateCommandAsync(Sql, ct);
        AddParameter(command, "map_object_id", NpgsqlDbType.Uuid, mapObjectId);
        AddParameter(command, "lats", NpgsqlDbType.Array | NpgsqlDbType.Double, points.Select(p => p.Lat).ToArray());
        AddParameter(command, "lons", NpgsqlDbType.Array | NpgsqlDbType.Double, points.Select(p => p.Lon).ToArray());
        AddParameter(command, "distance", NpgsqlDbType.Double, (double)MaxDistanceMeters);

        var near = new HashSet<int>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            near.Add(reader.GetInt32(0));
        }

        return near;
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

    private sealed record ZoneRow(string CoverageKind, GeoBox Box);

    private sealed record AddressMessage(string Address, CardStatusView Status, DateTimeOffset ObservedAt, string? Text);
}

/// <summary>Ответ <c>GET /map/objects/{id}/points</c> (схема <c>ZonePointsResponse</c>).</summary>
/// <param name="Items">Точки по адресам.</param>
public sealed record ZonePointsResponse(IReadOnlyList<ZonePointView> Items);

/// <summary>Временная точка по адресу из сообщений зоны (схема <c>ZonePoint</c>).</summary>
/// <param name="Address">Адрес, как его вернула LLM (по-русски).</param>
/// <param name="Location">Координаты от геокодера.</param>
/// <param name="Status">Статус самого свежего сообщения по адресу.</param>
/// <param name="ObservedAt">Время самого свежего сообщения.</param>
/// <param name="Text">Текст самого свежего сообщения.</param>
/// <param name="ObservationCount">Сколько актуальных сообщений по адресу.</param>
public sealed record ZonePointView(
    string Address,
    ZonePointLocation Location,
    CardStatusView Status,
    DateTimeOffset ObservedAt,
    string? Text,
    int ObservationCount);

/// <summary>Координаты точки, WGS 84.</summary>
/// <param name="Lat">Широта.</param>
/// <param name="Lon">Долгота.</param>
public sealed record ZonePointLocation(double Lat, double Lon);
