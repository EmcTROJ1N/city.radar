using BuildingBlocks.Persistence;

using Ingestion.Abstractions;

using Microsoft.EntityFrameworkCore;

using Sources.Contracts;
using Sources.Core.Domain;

namespace Sources.Core.Health;

/// <summary>
/// Запись health-check источника: состояние, деталь и время последнего
/// сообщения пишутся прямо в <c>sources</c>. Health — служебные данные Worker'а, поэтому аудит
/// не создаётся. Запись идёт одним UPDATE без загрузки сущности и без гонок с остальными
/// полями источника.
/// </summary>
internal sealed class SourceHealthWriter(MayakDbContext db) : ISourceHealthReporter
{
    /// <inheritdoc />
    public async Task ReportAsync(
        Guid sourceId,
        SourceState state,
        string? detail,
        DateTimeOffset? lastMessageAt,
        CancellationToken ct)
    {
        var databaseValue = state.ToDatabaseValue();

        // Null lastMessageAt означает «новых сообщений не было»: уже известное время не стираем,
        // иначе включённый источник навсегда остался бы без вычисляемого silent.
        await db.Set<Source>()
            .Where(source => source.Id == sourceId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(source => source.State, databaseValue)
                    .SetProperty(source => source.StateDetail, detail)
                    .SetProperty(
                        source => source.LastMessageAt,
                        source => lastMessageAt ?? source.LastMessageAt),
                ct);
    }
}
