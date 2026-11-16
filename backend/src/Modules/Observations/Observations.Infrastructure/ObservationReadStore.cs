using System.Data;
using System.Data.Common;

using BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Npgsql;
using NpgsqlTypes;

using Observations.Application;

namespace Observations.Infrastructure;

/// <summary>
/// Read-only SQL-проекции наблюдений: публичный список отметок, текущий статус
/// фиксации и расстояние по PostGIS. Чужие таблицы (<c>source_statuses</c>, <c>sources</c>,
/// <c>map_objects</c>) не заводятся в модель наблюдений — читаются сырым SQL.
/// </summary>
internal sealed class ObservationReadStore(MayakDbContext db) : IObservationReadStore
{
    public async Task<IReadOnlyList<PublicObservationView>> GetPublicAsync(
        Guid mapObjectId,
        int offset,
        int limit,
        CancellationToken ct)
    {
        const string Sql = """
            select o.id,
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
            offset @offset limit @limit
            """;

        await using var command = await CreateCommandAsync(Sql, ct);

        AddParameter(command, "map_object_id", NpgsqlDbType.Uuid, mapObjectId);
        AddParameter(command, "offset", NpgsqlDbType.Integer, offset);
        AddParameter(command, "limit", NpgsqlDbType.Integer, limit);

        var items = new List<PublicObservationView>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new PublicObservationView(
                reader.GetGuid(0),
                new PublicStatusView(
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5)),
                ReadTimestamp(reader, 6),
                ReadTimestamp(reader, 7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetFloat(11),
                reader.IsDBNull(12) ? null : reader.GetInt32(12)));
        }

        return items;
    }

    public async Task<CurrentStatusViewDto?> GetCurrentStatusAsync(
        Guid mapObjectId,
        CancellationToken ct)
    {
        const string Sql = """
            select cs.source_status_id,
                   ss.code,
                   ss.name,
                   ss.color,
                   ss.icon,
                   cs.freshness,
                   cs.observed_at,
                   cs.source_type,
                   cs.observation_count
            from current_statuses cs
            left join source_statuses ss on ss.id = cs.source_status_id
            where cs.map_object_id = @map_object_id
            """;

        await using var command = await CreateCommandAsync(Sql, ct);
        AddParameter(command, "map_object_id", NpgsqlDbType.Uuid, mapObjectId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        // «Неизвестно» в контракте — status: null, а не объект с null-полями.
        if (reader.IsDBNull(0) || reader.IsDBNull(1))
        {
            return null;
        }

        return new CurrentStatusViewDto(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : ReadTimestamp(reader, 6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetInt32(8));
    }

    public async Task<int?> GetGeoDistanceMAsync(
        Guid mapObjectId,
        double latitude,
        double longitude,
        CancellationToken ct)
    {
        const string Sql = """
            select round(
                     st_distance(
                       m.geometry,
                       st_setsrid(st_makepoint(@longitude, @latitude), 4326)::geography))
                   ::int
            from map_objects m
            where m.id = @map_object_id
            """;

        await using var command = await CreateCommandAsync(Sql, ct);

        AddParameter(command, "map_object_id", NpgsqlDbType.Uuid, mapObjectId);
        AddParameter(command, "latitude", NpgsqlDbType.Double, latitude);
        AddParameter(command, "longitude", NpgsqlDbType.Double, longitude);

        var value = await command.ExecuteScalarAsync(ct);

        return value is null or DBNull ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Команда на соединении контекста в его текущей транзакции.</summary>
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
}
