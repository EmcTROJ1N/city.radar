using BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;

using Sources.Contracts;

namespace Observations.Infrastructure;

/// <summary>
/// Реальная проверка удаления статуса источника: статус, по которому
/// есть наблюдения, не удаляется (409) — историю наблюдений не переписывают. Проверяется пара
/// <c>(source_id, source_status_id)</c>: идентификаторы статусов уникальны глобально, но условие
/// повторяет составной внешний ключ <c>observations</c> и не зависит от этого совпадения.
/// </summary>
internal sealed class SourceStatusObservationDeletionGuard(MayakDbContext db) : ISourceStatusDeletionGuard
{
    public Task<bool> HasObservationsAsync(Guid sourceId, Guid sourceStatusId, CancellationToken ct) =>
        db.Set<ObservationRecord>()
            .AsNoTracking()
            .AnyAsync(
                observation => observation.SourceId == sourceId
                    && observation.SourceStatusId == sourceStatusId,
                ct);
}
