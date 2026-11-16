using BuildingBlocks.Persistence;

using Catalog.Contracts;

using Microsoft.EntityFrameworkCore;

using Sources.Contracts;
using Sources.Core.Domain;

namespace Sources.Core.Services;

/// <summary>
/// Системный источник «Сайт». Один на категорию: в <c>map_object_sources</c> не пишется,
/// а добавляется обработчиком отметок и карточкой фиксации неявно по категории.
/// </summary>
internal sealed class SiteSourceProvisioner(
    MayakDbContext db,
    IStatusDefinitionDirectory statusDefinitions) : ISiteSourceProvisioner
{
    /// <summary>Код типа источника в <c>source_types</c>.</summary>
    public const string SourceTypeCode = "site";

    /// <summary>Префикс названия: «Сайт · <название категории>».</summary>
    public const string NamePrefix = "Сайт · ";

    /// <summary>
    /// Таймаут актуальности по умолчанию. Ручная отметка «на месте» быстро устаревает;
    /// модератор может изменить его в админке.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(3);

    public async Task EnsureForCategoryAsync(
        Guid categoryId,
        string categoryName,
        CancellationToken ct)
    {
        var name = NamePrefix + categoryName;

        var source = await db.Set<Source>()
            .Include(item => item.Policy)
            .FirstOrDefaultAsync(
                item => item.SourceType == SourceTypeCode && item.CategoryId == categoryId,
                ct);

        if (source is null)
        {
            source = new Source
            {
                Id = Guid.NewGuid(),
                Name = name,
                SourceType = SourceTypeCode,
                CategoryId = categoryId,
                Settings = "{}",
                UserPrompt = string.Empty,
                LinkByText = false,
                IsEnabled = true,
                State = "unknown",
                Policy = new SourcePolicy
                {
                    Timeout = DefaultTimeout,
                    ConfidenceThreshold = null,
                },
            };
            source.Policy.SourceId = source.Id;
            db.Add(source);
        }
        else
        {
            // Переименование категории меняет и название «Сайта».
            source.Name = name;

            if (source.Policy is null)
            {
                source.Policy = new SourcePolicy
                {
                    SourceId = source.Id,
                    Timeout = DefaultTimeout,
                };
                db.Add(source.Policy);
            }
        }

        await SyncStatusesAsync(source, categoryId, ct);
    }

    /// <summary>
    /// Добавляет недостающие статусы из справочника категории. Существующие не меняются:
    /// модератор редактирует статусы «Сайта» как у любого источника.
    /// </summary>
    private async Task SyncStatusesAsync(Source source, Guid categoryId, CancellationToken ct)
    {
        var definitions = await statusDefinitions.GetByCategoryAsync(categoryId, ct);
        if (definitions.Count == 0)
        {
            return;
        }

        var existing = await db.Set<SourceStatus>()
            .Where(status => status.SourceId == source.Id)
            .Select(status => status.Code)
            .ToListAsync(ct);
        var known = new HashSet<string>(existing, StringComparer.Ordinal);

        foreach (var definition in definitions)
        {
            if (!known.Add(definition.Code))
            {
                continue;
            }

            db.Add(new SourceStatus
            {
                Id = Guid.NewGuid(),
                SourceId = source.Id,
                StatusDefinitionId = definition.Id,
                Code = definition.Code,
                Name = definition.Name,
                Description = string.Empty,
                Color = definition.Color,
                Icon = definition.Icon,
                SortOrder = definition.Severity,
            });
        }
    }
}
