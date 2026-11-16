using BuildingBlocks.Persistence;

using Catalog.Contracts;
using Catalog.Core.Domain;

using Microsoft.EntityFrameworkCore;

namespace Catalog.Core.Services;

/// <summary>
/// Реализация <see cref="IStatusDefinitionDirectory"/>: другие модули читают справочник
/// статусов категории только через контракт.
/// </summary>
internal sealed class StatusDefinitionDirectory(MayakDbContext db) : IStatusDefinitionDirectory
{
    public async Task<IReadOnlyList<StatusDefinitionInfo>> GetByCategoryAsync(
        Guid categoryId,
        CancellationToken ct) =>
        await db.Set<StatusDefinition>()
            .AsNoTracking()
            .Where(definition => definition.CategoryId == categoryId)
            .OrderBy(definition => definition.Severity)
            .ThenBy(definition => definition.Name)
            .Select(definition => new StatusDefinitionInfo(
                definition.Id,
                definition.Code,
                definition.Name,
                definition.Color,
                definition.Icon,
                definition.Severity))
            .ToListAsync(ct);
}
