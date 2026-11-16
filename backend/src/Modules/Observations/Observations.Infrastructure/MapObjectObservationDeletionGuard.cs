using BuildingBlocks.Persistence;

using Catalog.Contracts;

using Microsoft.EntityFrameworkCore;

namespace Observations.Infrastructure;

/// <summary>
/// Реальная проверка удаления фиксации: фиксацию с историей наблюдений
/// нельзя удалить (409), её деактивируют (<c>isActive: false</c>). Регистрация перекрывает
/// заглушку <c>Catalog</c> (<c>TryAddScoped</c>), код каталога при этом не меняется.
/// </summary>
internal sealed class MapObjectObservationDeletionGuard(MayakDbContext db) : IMapObjectDeletionGuard
{
    public Task<bool> HasObservationsAsync(Guid mapObjectId, CancellationToken ct) =>
        db.Set<ObservationRecord>()
            .AsNoTracking()
            .AnyAsync(observation => observation.MapObjectId == mapObjectId, ct);
}
