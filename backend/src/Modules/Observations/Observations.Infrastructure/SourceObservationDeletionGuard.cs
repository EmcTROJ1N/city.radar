using BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;

using Sources.Contracts;

namespace Observations.Infrastructure;

/// <summary>
/// Реальная проверка удаления источника: источник с историей
/// наблюдений не удаляется (409), его отключают через <c>/sources/{id}/disable</c>. Регистрация
/// в <see cref="ObservationsServiceCollectionExtensions.AddObservations"/> перекрывает заглушку
/// <c>Sources</c>, зарегистрированную через <c>TryAddScoped</c>.
/// </summary>
internal sealed class SourceObservationDeletionGuard(MayakDbContext db) : ISourceDeletionGuard
{
    public Task<bool> HasObservationsAsync(Guid sourceId, CancellationToken ct) =>
        db.Set<ObservationRecord>()
            .AsNoTracking()
            .AnyAsync(observation => observation.SourceId == sourceId, ct);
}
