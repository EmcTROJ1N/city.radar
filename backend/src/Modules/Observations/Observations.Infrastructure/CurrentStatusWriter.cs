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
/// Запись проекции <c>current_statuses</c>. Upsert строки выполняется под
/// <c>pg_advisory_xact_lock(hashtext(mapObjectId::text))</c>: одновременные пересчёты одной
/// фиксации сериализуются, разные фиксации друг друга не блокируют. Блокировка транзакционная,
/// поэтому снимается вместе с транзакцией вызывающего (или собственной транзакцией метода).
/// </summary>
/// <remarks>
/// Запись идёт SQL-ом, а не через change tracking: <c>on conflict ... where ... is distinct from</c>
/// обновляет <c>updated_at</c> только при изменении итога, иначе ETag (по
/// <c>max(current_statuses.updated_at)</c>) прыгал бы на каждом пересчёте без причины (Review Focus 3).
/// </remarks>
internal sealed class CurrentStatusWriter(MayakDbContext db) : ICurrentStatusWriter
{
    public async Task WriteAsync(
        Guid mapObjectId,
        DateTimeOffset now,
        Func<CancellationToken, Task<CurrentStatusView>> calculate,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(calculate);

        // Отметка/отзыв уже открывают транзакцию (–4.5): пересчёт обязан идти в ней,
        // чтобы наблюдение, проекция и аудит сохранились атомарно.
        var ownTransaction = db.Database.CurrentTransaction is null;
        var transaction = ownTransaction
            ? await db.Database.BeginTransactionAsync(ct)
            : db.Database.CurrentTransaction!;

        try
        {
            await AcquireLockAsync(mapObjectId, ct);

            // Факты читаются уже под блокировкой: иначе результат мог бы быть посчитан по
            // устаревшему снимку и перезаписать более свежий пересчёт (Review Focus 2).
            var status = await calculate(ct);

            await UpsertAsync(mapObjectId, status, now, ct);

            if (ownTransaction)
            {
                await transaction.CommitAsync(ct);
            }
        }
        catch
        {
            if (ownTransaction)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw;
        }
        finally
        {
            if (ownTransaction)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    /// <summary>Транзакционная блокировка по ключу фиксации; живёт до конца транзакции.</summary>
    private async Task AcquireLockAsync(Guid mapObjectId, CancellationToken ct)
    {
        await using var command = CreateCommand(
            "select pg_advisory_xact_lock(hashtext(cast(@map_object_id as text)))");

        AddParameter(command, "map_object_id", NpgsqlDbType.Uuid, mapObjectId);

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Upsert проекции: строка создаётся всегда (в том числе «неизвестно» при отсутствии
    /// актуальных наблюдений), а <c>updated_at</c> меняется только при изменении итога.
    /// </summary>
    private async Task UpsertAsync(
        Guid mapObjectId,
        CurrentStatusView status,
        DateTimeOffset now,
        CancellationToken ct)
    {
        const string Sql = """
            insert into current_statuses as target
              (map_object_id, source_status_id, source_id, observation_id, observed_at,
               freshness, source_type, observation_count, updated_at)
            values
              (@map_object_id, @source_status_id, @source_id, @observation_id, @observed_at,
               @freshness, @source_type, @observation_count, @updated_at)
            on conflict (map_object_id) do update set
              source_status_id = excluded.source_status_id,
              source_id = excluded.source_id,
              observation_id = excluded.observation_id,
              observed_at = excluded.observed_at,
              freshness = excluded.freshness,
              source_type = excluded.source_type,
              observation_count = excluded.observation_count,
              updated_at = excluded.updated_at
            where (target.source_status_id, target.source_id, target.observation_id, target.observed_at,
                   target.freshness, target.source_type, target.observation_count)
              is distinct from
                  (excluded.source_status_id, excluded.source_id, excluded.observation_id, excluded.observed_at,
                   excluded.freshness, excluded.source_type, excluded.observation_count)
            """;

        await using var command = CreateCommand(Sql);

        AddParameter(command, "map_object_id", NpgsqlDbType.Uuid, mapObjectId);
        AddParameter(command, "source_status_id", NpgsqlDbType.Uuid, status.SourceStatusId);
        AddParameter(command, "source_id", NpgsqlDbType.Uuid, status.SourceId);
        AddParameter(command, "observation_id", NpgsqlDbType.Uuid, status.ObservationId);
        AddParameter(command, "observed_at", NpgsqlDbType.TimestampTz, status.ObservedAt);
        AddParameter(command, "freshness", NpgsqlDbType.Text, ToDb(status.Freshness));
        AddParameter(
            command,
            "source_type",
            NpgsqlDbType.Text,
            status.Kind is { } kind ? ToDb(kind) : null);
        AddParameter(command, "observation_count", NpgsqlDbType.Integer, status.ObservationCount);
        AddParameter(command, "updated_at", NpgsqlDbType.TimestampTz, now);

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Команда на соединении контекста в его текущей транзакции.</summary>
    private DbCommand CreateCommand(string sql)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

        return command;
    }

    private static void AddParameter(DbCommand command, string name, NpgsqlDbType type, object? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
    }

    private static string ToDb(Freshness freshness) => freshness switch
    {
        Freshness.Fresh => "fresh",
        Freshness.Aging => "aging",
        Freshness.Stale => "stale",
        _ => throw new ArgumentOutOfRangeException(nameof(freshness), freshness, "Неизвестная свежесть."),
    };

    private static string ToDb(SourceKind kind) => kind switch
    {
        SourceKind.Manual => "manual",
        SourceKind.Moderator => "moderator",
        SourceKind.Telegram => "telegram",
        SourceKind.Max => "max",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный тип источника."),
    };
}
