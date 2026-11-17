using System.Globalization;
using System.Text.Json;

using BuildingBlocks.Persistence;
using BuildingBlocks.Web;

using Catalog.Contracts;
using Catalog.Core.Domain;
using Catalog.Core.Endpoints;

using IdentityAccess.Contracts;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

using Sources.Contracts;

namespace Catalog.Core.Services;

/// <summary>
/// Фиксации на карте: CRUD, фильтры редакторского списка,
/// привязка и отвязка источников. Каждое изменение пишет запись аудита в том же
/// <c>SaveChanges</c>, что и бизнес-изменение.
/// </summary>
internal sealed class MapObjectService(
    MayakDbContext db,
    IAuditLog audit,
    ICurrentUser currentUser,
    IMapObjectDeletionGuard deletionGuard,
    ISourceDirectory sourceDirectory)
{
    private const string EntityType = "map_object";
    private const string GeometryKindPoint = "point";
    private const string GeometryKindPolygon = "polygon";
    private const string SiteSourceType = "site";
    private const string SiteSourceNotLinkableMessage =
        "«Сайт» доступен фиксациям неявно и не привязывается к ним";

    /// <summary>Редакторский список фиксаций: фильтры контракта и курсорная страница.</summary>
    public async Task<IResult> ListAsync(
        MapObjectFilter filter,
        PageRequest page,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);

        if (filter.Kind is not null
            && filter.Kind is not (GeometryKindPoint or GeometryKindPolygon))
        {
            return ProblemResponses.BadRequest(
                "Параметр kind должен быть point или polygon");
        }

        if (!TryReadOffset(page, out var offset))
        {
            return ProblemResponses.BadRequest("Некорректный курсор");
        }

        var query = db.Set<MapObject>().AsNoTracking();

        if (filter.SourceId is { } sourceId)
        {
            query = query.Where(mapObject => db.Set<MapObjectSource>()
                .Any(link => link.MapObjectId == mapObject.Id && link.SourceId == sourceId));
        }

        if (filter.GroupId is { } groupId)
        {
            query = query.Where(mapObject => mapObject.GroupId == groupId);
        }

        if (filter.ObjectTypeId is { } objectTypeId)
        {
            query = query.Where(mapObject => mapObject.ObjectTypeId == objectTypeId);
        }

        // categoryId фильтрует через тип объекта: у самой фиксации категории нет.
        if (filter.CategoryId is { } categoryId)
        {
            query = query.Where(mapObject => mapObject.ObjectTypeId != null
                && db.Set<ObjectType>()
                    .Any(objectType => objectType.Id == mapObject.ObjectTypeId
                        && objectType.CategoryId == categoryId));
        }

        if (filter.Kind is { } kind)
        {
            query = query.Where(mapObject => mapObject.CoverageKind == kind);
        }

        if (filter.IsActive is { } isActive)
        {
            query = query.Where(mapObject => mapObject.IsActive == isActive);
        }

        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            // pg_trgm ускоряет именно ILIKE '%...%' (индекс map_objects_name_trgm).
            var pattern = $"%{EscapeLikePattern(filter.Query.Trim())}%";
            query = query.Where(mapObject => EF.Functions.ILike(mapObject.Name, pattern, "\\"));
        }

        var rows = await query
            .OrderBy(mapObject => mapObject.Name)
            .ThenBy(mapObject => mapObject.Id)
            .Skip(offset)
            .Take(page.Limit + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > page.Limit;
        var items = hasMore ? rows.Take(page.Limit).ToList() : rows;
        var sourceIds = await ReadSourceIdsAsync(items.Select(item => item.Id).ToArray(), ct);

        var responses = items
            .Select(item => ToResponse(item, sourceIds.GetValueOrDefault(item.Id, [])))
            .ToArray();

        var nextCursor = hasMore
            ? PageRequest.EncodeCursor(offset + items.Count)
            : null;

        return Results.Ok(new Page<MapObjectResponse>(responses, nextCursor));
    }

    /// <summary>Фиксация в редакторском виде.</summary>
    public async Task<IResult> GetAsync(Guid id, CancellationToken ct)
    {
        var mapObject = await db.Set<MapObject>()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (mapObject is null)
        {
            return ProblemResponses.NotFound($"Фиксация {id} не существует");
        }

        var sourceIds = await db.Set<MapObjectSource>()
            .AsNoTracking()
            .Where(link => link.MapObjectId == id)
            .Select(link => link.SourceId)
            .ToListAsync(ct);

        return Results.Ok(ToResponse(mapObject, sourceIds));
    }

    /// <summary>Создание фиксации; источники из <c>sourceIds</c> привязываются сразу.</summary>
    public async Task<IResult> CreateAsync(MapObjectInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var errors = await ValidateReferencesAsync(input, ct);
        if (errors.Count > 0)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        var entity = new MapObject
        {
            Id = Guid.NewGuid(),
            ObjectTypeId = input.ObjectTypeId,
            GroupId = input.GroupId,
            Name = input.Name,
            CoverageKind = input.CoverageKind,
            Geometry = input.Geometry,
            Address = input.Address,
            IsActive = input.IsActive,
            CreatedBy = currentUser.UserId,
        };

        db.Add(entity);

        foreach (var sourceId in input.SourceIds)
        {
            db.Add(new MapObjectSource { MapObjectId = entity.Id, SourceId = sourceId });
        }

        await audit.WriteAsync(
            "catalog.object.create",
            EntityType,
            entity.Id.ToString(),
            null,
            ToAuditSnapshot(entity),
            ct: ct);

        var conflict = await CatalogPersistence.SaveAsync(
            db,
            "Фиксация уже существует",
            "Тип объекта или группа не существует",
            ct);
        if (conflict is not null)
        {
            return conflict;
        }

        return Results.Created(
            $"/api/v1/catalog/objects/{entity.Id}",
            ToResponse(entity, input.SourceIds));
    }

    /// <summary>
    /// Изменение фиксации. Привязки источников меняются отдельными методами
    /// (<c>/catalog/objects/{id}/sources/{sourceId}</c>), поле <c>sourceIds</c> здесь игнорируется.
    /// </summary>
    public async Task<IResult> UpdateAsync(Guid id, MapObjectInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var entity = await db.Set<MapObject>()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return ProblemResponses.NotFound($"Фиксация {id} не существует");
        }

        var errors = await ValidateReferencesAsync(input, ct);
        if (errors.Count > 0)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        var before = ToAuditSnapshot(entity);

        entity.ObjectTypeId = input.ObjectTypeId;
        entity.GroupId = input.GroupId;
        entity.Name = input.Name;
        entity.CoverageKind = input.CoverageKind;
        entity.Geometry = input.Geometry;
        entity.Address = input.Address;
        entity.IsActive = input.IsActive;

        await audit.WriteAsync(
            "catalog.object.update",
            EntityType,
            entity.Id.ToString(),
            before,
            ToAuditSnapshot(entity),
            ct: ct);

        var conflict = await CatalogPersistence.SaveAsync(
            db,
            "Фиксация уже существует",
            "Тип объекта или группа не существует",
            ct);
        if (conflict is not null)
        {
            return conflict;
        }

        var sourceIds = await db.Set<MapObjectSource>()
            .AsNoTracking()
            .Where(link => link.MapObjectId == id)
            .Select(link => link.SourceId)
            .ToListAsync(ct);

        return Results.Ok(ToResponse(entity, sourceIds));
    }

    /// <summary>
    /// Удаление фиксации. Если у неё есть наблюдения, удаление запрещено (409): фиксацию
    /// деактивируют (<c>isActive: false</c>), чтобы история сохранилась.
    /// </summary>
    public async Task<IResult> DeleteAsync(Guid id, CancellationToken ct)
    {
        var entity = await db.Set<MapObject>()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return ProblemResponses.NotFound($"Фиксация {id} не существует");
        }

        if (await deletionGuard.HasObservationsAsync(id, ct))
        {
            return ProblemResponses.Conflict(
                "У фиксации есть наблюдения, удалить её нельзя: снимите isActive (isActive: false), чтобы скрыть её с карты");
        }

        var before = ToAuditSnapshot(entity);
        db.Remove(entity);

        await audit.WriteAsync(
            "catalog.object.delete",
            EntityType,
            entity.Id.ToString(),
            before,
            null,
            ct: ct);

        var conflict = await CatalogPersistence.SaveAsync(
            db,
            "Фиксация уже существует",
            "Фиксация используется другими записями и не может быть удалена",
            ct);
        if (conflict is not null)
        {
            return conflict;
        }

        return Results.NoContent();
    }

    /// <summary>
    /// Привязка источника к фиксации. Операция идемпотентна: повтор возвращает тот же 204.
    /// Несуществующий источник — 404 по контракту (проверка через <see cref="ISourceDirectory"/>,
    /// прямая ссылка на <c>Sources.Core</c> запрещена).
    /// </summary>
    public async Task<IResult> LinkSourceAsync(Guid id, Guid sourceId, CancellationToken ct)
    {
        var exists = await db.Set<MapObject>()
            .AsNoTracking()
            .AnyAsync(item => item.Id == id, ct);
        if (!exists)
        {
            return ProblemResponses.NotFound($"Фиксация {id} не существует");
        }

        if (!await sourceDirectory.ExistsAsync(sourceId, ct))
        {
            return ProblemResponses.NotFound($"Источник {sourceId} не существует");
        }

        var policy = await sourceDirectory.GetPolicyAsync(sourceId, ct);
        if (string.Equals(policy?.SourceType, SiteSourceType, StringComparison.Ordinal))
        {
            return ProblemResponses.Validation(
                new ValidationError("sourceId", SiteSourceNotLinkableMessage));
        }

        var linked = await db.Set<MapObjectSource>()
            .FirstOrDefaultAsync(
                link => link.MapObjectId == id && link.SourceId == sourceId,
                ct);
        if (linked is not null)
        {
            return Results.NoContent();
        }

        db.Add(new MapObjectSource { MapObjectId = id, SourceId = sourceId });

        await audit.WriteAsync(
            "catalog.object.source.link",
            EntityType,
            id.ToString(),
            null,
            new { sourceId },
            ct: ct);

        var conflict = await CatalogPersistence.SaveAsync(
            db,
            "Источник уже привязан к фиксации",
            "Фиксация или источник не существует",
            ct);
        if (conflict is not null)
        {
            return conflict;
        }

        return Results.NoContent();
    }

    /// <summary>Отвязка источника; непривязанный источник — 404 по контракту.</summary>
    public async Task<IResult> UnlinkSourceAsync(Guid id, Guid sourceId, CancellationToken ct)
    {
        var exists = await db.Set<MapObject>()
            .AsNoTracking()
            .AnyAsync(item => item.Id == id, ct);
        if (!exists)
        {
            return ProblemResponses.NotFound($"Фиксация {id} не существует");
        }

        var linked = await db.Set<MapObjectSource>()
            .FirstOrDefaultAsync(
                link => link.MapObjectId == id && link.SourceId == sourceId,
                ct);
        if (linked is null)
        {
            return ProblemResponses.NotFound($"Источник {sourceId} не привязан к фиксации {id}");
        }

        db.Remove(linked);

        await audit.WriteAsync(
            "catalog.object.source.unlink",
            EntityType,
            id.ToString(),
            new { sourceId },
            null,
            ct: ct);

        var conflict = await CatalogPersistence.SaveAsync(
            db,
            "Источник уже привязан к фиксации",
            "Фиксация или источник не существует",
            ct);
        if (conflict is not null)
        {
            return conflict;
        }

        return Results.NoContent();
    }

    /// <summary>
    /// Ссылочные проверки входа: тип объекта (geometryKind), группа
    /// и привязанные источники. Несуществующий <c>objectTypeId</c>/<c>groupId</c>/источник —
    /// 422 по контракту.
    /// </summary>
    private async Task<List<ValidationError>> ValidateReferencesAsync(
        MapObjectInput input,
        CancellationToken ct)
    {
        var errors = new List<ValidationError>();

        foreach (var sourceId in input.SourceIds)
        {
            var policy = await sourceDirectory.GetPolicyAsync(sourceId, ct);
            if (policy is null)
            {
                errors.Add(new ValidationError("sourceIds", $"Источник {sourceId} не существует"));
            }
            else if (string.Equals(policy.SourceType, SiteSourceType, StringComparison.Ordinal))
            {
                errors.Add(new ValidationError("sourceIds", SiteSourceNotLinkableMessage));
            }
        }

        if (input.GroupId is { } groupId)
        {
            var groupExists = await db.Set<ObjectGroup>()
                .AsNoTracking()
                .AnyAsync(group => group.Id == groupId, ct);
            if (!groupExists)
            {
                errors.Add(new ValidationError("groupId", $"Группа {groupId} не существует"));
            }
        }

        if (input.ObjectTypeId is not { } objectTypeId)
        {
            return errors;
        }

        var objectType = await db.Set<ObjectType>()
            .AsNoTracking()
            .FirstOrDefaultAsync(type => type.Id == objectTypeId, ct);
        if (objectType is null)
        {
            errors.Add(new ValidationError("objectTypeId", $"Тип объекта {objectTypeId} не существует"));

            return errors;
        }

        if (objectType.GeometryKind == GeometryKindPoint && input.CoverageKind == GeometryKindPolygon)
        {
            errors.Add(new ValidationError(
                "geometry",
                "Тип объекта принимает только точку, полигон недопустим"));
        }

        if (objectType.GeometryKind == GeometryKindPolygon && input.CoverageKind == GeometryKindPoint)
        {
            errors.Add(new ValidationError(
                "geometry",
                "Тип объекта принимает только полигон, точка недопустима"));
        }

        return errors;
    }

    private async Task<Dictionary<Guid, IReadOnlyList<Guid>>> ReadSourceIdsAsync(
        Guid[] mapObjectIds,
        CancellationToken ct)
    {
        if (mapObjectIds.Length == 0)
        {
            return [];
        }

        var links = await db.Set<MapObjectSource>()
            .AsNoTracking()
            .Where(link => mapObjectIds.Contains(link.MapObjectId))
            .ToListAsync(ct);

        return links
            .GroupBy(link => link.MapObjectId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<Guid>)[.. group.Select(link => link.SourceId)]);
    }

    /// <summary>Курсор редакторского списка — непрозрачная позиция страницы (смещение).</summary>
    private static bool TryReadOffset(PageRequest page, out int offset)
    {
        offset = 0;

        if (!page.HasCursor)
        {
            return true;
        }

        return int.TryParse(
                page.CursorPayload,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out offset)
            && offset >= 0;
    }

    /// <summary>
    /// Экранирует <c>%</c>, <c>_</c> и <c>\</c>, чтобы они искались буквально: иначе
    /// пользовательский ввод с <c>%</c> превратился бы в шаблон поиска.
    /// </summary>
    private static string EscapeLikePattern(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static MapObjectResponse ToResponse(
        MapObject mapObject,
        IReadOnlyList<Guid> sourceIds) =>
        new(
            mapObject.Id,
            mapObject.Name,
            mapObject.ObjectTypeId,
            mapObject.GroupId,
            mapObject.CoverageKind,
            GeoJson.ToGeoJson(mapObject.Geometry),
            mapObject.Address,
            mapObject.IsActive,
            sourceIds);

    /// <summary>Состояние для аудита: геометрия — WKT, чтобы запись оставалась компактной.</summary>
    private static MapObjectAuditSnapshot ToAuditSnapshot(MapObject mapObject) =>
        new(
            mapObject.Name,
            mapObject.ObjectTypeId,
            mapObject.GroupId,
            mapObject.CoverageKind,
            mapObject.Geometry.AsText(),
            mapObject.Address,
            mapObject.IsActive);
}

/// <summary>Фильтры редакторского списка фиксаций (<c>GET /catalog/objects</c>).</summary>
internal sealed record MapObjectFilter(
    Guid? SourceId,
    Guid? GroupId,
    Guid? CategoryId,
    Guid? ObjectTypeId,
    string? Kind,
    bool? IsActive,
    string? Query);

/// <summary>Элемент ответа по схеме <c>MapObject</c>.</summary>
internal sealed record MapObjectResponse(
    Guid Id,
    string Name,
    Guid? ObjectTypeId,
    Guid? GroupId,
    string CoverageKind,
    JsonElement Geometry,
    string? Address,
    bool IsActive,
    IReadOnlyList<Guid> SourceIds);

/// <summary>Снимок фиксации для <c>audit_entries.before/after</c>.</summary>
internal sealed record MapObjectAuditSnapshot(
    string Name,
    Guid? ObjectTypeId,
    Guid? GroupId,
    string CoverageKind,
    string Geometry,
    string? Address,
    bool IsActive);
