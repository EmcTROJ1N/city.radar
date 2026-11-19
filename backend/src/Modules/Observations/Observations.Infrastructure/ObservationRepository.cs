using System.Data;
using System.Data.Common;

using BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Npgsql;
using NpgsqlTypes;

using Observations.Application;
using Observations.Domain;

namespace Observations.Infrastructure;

/// <summary>
/// Реализация <see cref="IObservationRepository"/> на едином <see cref="MayakDbContext"/>
///. Изменяющие методы работают с текущим контекстом и не сохраняют изменения:
/// вызывающий обработчик фиксирует наблюдение, пересчёт проекции и аудит одной транзакцией
///.
/// </summary>
internal sealed class ObservationRepository(MayakDbContext db) : IObservationRepository
{
    public Task AddAsync(Observation observation, CancellationToken ct, int? geoDistanceM = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ct.ThrowIfCancellationRequested();

        var record = ObservationRecord.FromDomain(observation);
        record.GeoDistanceM = geoDistanceM;

        db.Set<ObservationRecord>().Add(record);

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<ObservationFact>> GetFactsAsync(
        Guid mapObjectId,
        CancellationToken ct)
    {
        // Таймаут берётся из source_policies источника: таблица принадлежит модулю
        // источников, поэтому читаем её read-only SQL-проекцией, не заводя чужую сущность
        // в модель. Отозванные строки отфильтрованы в запросе; истёкшие — резолвер.
        // Цвет статуса и «частичный» статус источника (первый limited по порядку показа)
        // нужны только сводке по зоне.
        const string Sql = """
            select o.id,
                   o.source_id,
                   o.source_status_id,
                   o.source_type,
                   o.observed_at,
                   o.received_at,
                   o.expires_at,
                   p.timeout,
                   o.is_retracted,
                   ss.color,
                   (select ls.id
                    from source_statuses ls
                    where ls.source_id = o.source_id and ls.color = 'limited'
                    order by ls.sort_order, ls.id
                    limit 1)
            from observations o
            join source_policies p on p.source_id = o.source_id
            join source_statuses ss on ss.id = o.source_status_id
            where o.map_object_id = @map_object_id
              and not o.is_retracted
            order by o.observed_at desc, o.received_at desc
            """;

        var connection = db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;

        if (closeAfter)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = Sql;

            var parameter = command.CreateParameter();
            parameter.ParameterName = "map_object_id";
            parameter.Value = mapObjectId;
            command.Parameters.Add(parameter);

            var facts = new List<ObservationFact>();

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                facts.Add(new ObservationFact(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    ObservationRecord.ParseKind(reader.GetString(3)),
                    ReadTimestamp(reader, 4),
                    ReadTimestamp(reader, 5),
                    ReadTimestamp(reader, 6),
                    reader.GetFieldValue<TimeSpan>(7),
                    reader.GetBoolean(8),
                    ParseTone(reader.GetString(9)),
                    reader.IsDBNull(10) ? null : reader.GetGuid(10)));
            }

            return facts;
        }
        finally
        {
            if (closeAfter)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    public async Task<bool> IsZoneAsync(Guid mapObjectId, CancellationToken ct)
    {
        // map_objects — таблица каталога: читаем один признак SQL-проекцией.
        var kinds = await db.Database
            .SqlQuery<string>($"select coverage_kind as \"Value\" from map_objects where id = {mapObjectId}")
            .ToListAsync(ct);

        return kinds is ["polygon"];
    }

    public async Task<Observation?> GetAsync(Guid id, CancellationToken ct)
    {
        var record = await db.Set<ObservationRecord>()
            .AsNoTracking()
            .FirstOrDefaultAsync(observation => observation.Id == id, ct);

        return record?.ToDomain();
    }

    public async Task RetractAsync(
        Guid id,
        Guid by,
        string reason,
        DateTimeOffset at,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var record = await db.Set<ObservationRecord>()
            .FirstOrDefaultAsync(observation => observation.Id == id, ct)
            ?? throw new DomainException("Наблюдение не найдено.");

        if (record.IsRetracted)
        {
            throw new DomainException("Наблюдение уже отозвано.");
        }

        // Меняются только поля отзыва: триггер observations_guard пропустит ровно этот набор
        //, остальное он считает неизменяемым.
        record.IsRetracted = true;
        record.RetractedBy = by;
        record.RetractedAt = at;
        record.RetractReason = reason;
    }

    public async Task<long> CountRetractableAsync(BulkRetractFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var (whereSql, parameters) = BuildRetractableWhere(filter);

        await using var command = await CreateCommandAsync(
            $"select count(*) from observations o where not o.is_retracted {whereSql}",
            ct);
        AddFilterParameters(command, parameters);

        var value = await command.ExecuteScalarAsync(ct);

        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<RetractedObservationRow>> RetractBulkAsync(
        BulkRetractFilter filter,
        Guid by,
        string reason,
        DateTimeOffset at,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var (whereSql, parameters) = BuildRetractableWhere(filter);

        await using var command = await CreateCommandAsync(
            $"""
             update observations o
             set is_retracted = true,
                 retracted_by = @by,
                 retracted_at = @at,
                 retract_reason = @reason
             where not o.is_retracted {whereSql}
             returning o.id, o.map_object_id
             """,
            ct);

        command.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = by });
        command.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at });
        command.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Text) { Value = reason });
        AddFilterParameters(command, parameters);

        var rows = new List<RetractedObservationRow>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new RetractedObservationRow(reader.GetGuid(0), reader.GetGuid(1)));
        }

        return rows;
    }

    /// <summary>
    /// Условие отбора пакетного отзыва по <c>reporter_id</c>, <c>source_id</c> и периоду
    /// <c>observed_at</c>. Возвращает SQL-фрагмент и параметры; пустой фильтр отвергается
    /// вызывающим обработчиком, но и здесь не превращается в «все строки».
    /// </summary>
    private static (string Sql, List<NpgsqlParameter> Parameters) BuildRetractableWhere(
        BulkRetractFilter filter)
    {
        if (!filter.HasAny)
        {
            throw new ArgumentException("Фильтр пакетного отзыва не может быть пустым.", nameof(filter));
        }

        var clauses = new List<string>();
        var parameters = new List<NpgsqlParameter>();

        if (filter.ReporterId is { } reporterId)
        {
            clauses.Add("and o.reporter_id = @reporter_id");
            parameters.Add(new NpgsqlParameter("reporter_id", NpgsqlDbType.Uuid) { Value = reporterId });
        }

        if (filter.SourceId is { } sourceId)
        {
            clauses.Add("and o.source_id = @source_id");
            parameters.Add(new NpgsqlParameter("source_id", NpgsqlDbType.Uuid) { Value = sourceId });
        }

        if (filter.From is { } from)
        {
            clauses.Add("and o.observed_at >= @from");
            parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = from });
        }

        if (filter.To is { } to)
        {
            clauses.Add("and o.observed_at <= @to");
            parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = to });
        }

        return (string.Join('\n', clauses), parameters);
    }

    private static void AddFilterParameters(
        DbCommand command,
        IReadOnlyList<NpgsqlParameter> parameters)
    {
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }
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

    /// <summary>Читает <c>timestamptz</c> как UTC <see cref="DateTimeOffset"/>.</summary>
    private static StatusTone ParseTone(string color) => color switch
    {
        "ok" => StatusTone.Positive,
        "none" => StatusTone.Negative,
        _ => StatusTone.Limited,
    };

    private static DateTimeOffset ReadTimestamp(DbDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetFieldValue<DateTime>(ordinal), DateTimeKind.Utc));
}
