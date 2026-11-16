using System.Data.Common;

using BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace IngestionWorker.Host;

/// <summary>
/// Минимальная обвязка над ADO.NET для таблиц Worker-а (<c>raw_messages</c>, настройки источников):
/// они читаются сырым SQL в текущем <see cref="MayakDbContext"/>, поэтому Worker не зависит
/// от internal-типов модулей Observations и Sources.
/// </summary>
internal static class WorkerSql
{
    /// <summary>
    /// Создаёт команду в соединении контекста; если контекст уже в транзакции, команда
    /// присоединяется к ней — тогда чтение и запись видят одно состояние БД.
    /// </summary>
    public static async Task<DbCommand> CreateCommandAsync(
        MayakDbContext db,
        string sql,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

        return command;
    }

    /// <summary>Добавляет параметр, представляя <c>null</c> как <see cref="DBNull"/>.</summary>
    public static void AddParameter(DbCommand command, string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(command);

        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
