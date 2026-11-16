using BuildingBlocks.Persistence;

using Catalog.Contracts;
using Catalog.Core.Domain;

using Microsoft.EntityFrameworkCore;

namespace Catalog.Core.Services;

/// <summary>
/// Реализация <see cref="IMapObjectDirectory"/>: другие модули читают привязки фиксаций
/// только через этот контракт.
/// </summary>
internal sealed class MapObjectDirectory(MayakDbContext db) : IMapObjectDirectory
{
    public async Task<IReadOnlyList<Guid>> GetSourceIdsAsync(Guid mapObjectId, CancellationToken ct) =>
        await db.Set<MapObjectSource>()
            .AsNoTracking()
            .Where(link => link.MapObjectId == mapObjectId)
            .Select(link => link.SourceId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<LinkedObject>> GetObjectsOfSourceAsync(Guid sourceId, CancellationToken ct) =>
        await db.Set<MapObjectSource>()
            .AsNoTracking()
            .Where(link => link.SourceId == sourceId)
            .Join(
                db.Set<MapObject>().AsNoTracking(),
                link => link.MapObjectId,
                mapObject => mapObject.Id,
                (_, mapObject) => mapObject)
            .OrderBy(mapObject => mapObject.Name)
            .Select(mapObject => new LinkedObject(mapObject.Id, mapObject.Name, mapObject.Address))
            .ToListAsync(ct);

    public Task<bool> ExistsAsync(Guid mapObjectId, CancellationToken ct) =>
        db.Set<MapObject>()
            .AsNoTracking()
            .AnyAsync(mapObject => mapObject.Id == mapObjectId, ct);

    public Task<MapObjectInfo?> GetInfoAsync(Guid mapObjectId, CancellationToken ct) =>
        db.Set<MapObject>()
            .AsNoTracking()
            .Where(mapObject => mapObject.Id == mapObjectId)
            .Select(mapObject => new MapObjectInfo(
                mapObject.Id,
                mapObject.IsActive,
                mapObject.GroupId))
            .FirstOrDefaultAsync(ct);
}
