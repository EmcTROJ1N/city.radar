using BuildingBlocks.Persistence;
using BuildingBlocks.Web;

using Catalog.Core.Domain;

using IdentityAccess.Contracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

using Sources.Contracts;

namespace Catalog.Core.Endpoints;

/// <summary>
/// <c>/catalog/categories</c>: публичный справочник категорий и управление
/// по праву <c>catalog.category.manage</c>. Удалить категорию, на которую ссылаются типы
/// объектов, группы или статусы, нельзя (409).
/// </summary>
public static class CategoriesEndpoints
{
    private const string EntityType = "category";

    /// <summary>Регистрирует эндпоинты категорий внутри текущего префикса (например, <c>/api/v1</c>).</summary>
    public static IEndpointRouteBuilder MapCategoriesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/catalog/categories");

        // Чтение публичное: в контракте у GET security: [] — анонимам разрешено.
        group.MapGet(string.Empty, ListAsync);

        group.MapPost(string.Empty, CreateAsync)
            .RequireAuthorization(Permissions.CatalogCategoryManage);
        group.MapPut("/{id:guid}", UpdateAsync)
            .RequireAuthorization(Permissions.CatalogCategoryManage);
        group.MapDelete("/{id:guid}", DeleteAsync)
            .RequireAuthorization(Permissions.CatalogCategoryManage);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(MayakDbContext db, CancellationToken ct)
    {
        var categories = await db.Set<Category>()
            .AsNoTracking()
            .OrderBy(category => category.SortOrder)
            .ThenBy(category => category.Name)
            .ToListAsync(ct);

        return Results.Ok(categories.Select(CatalogPersistence.ToResponse).ToArray());
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        MayakDbContext db,
        IAuditLog audit,
        ISiteSourceProvisioner siteSourceProvisioner,
        CancellationToken ct)
    {
        using var document = await CatalogRequestSupport.TryReadObjectAsync(context.Request, ct);
        if (document is null)
        {
            return ProblemResponses.Validation(
                new ValidationError("body", "Ожидается JSON-объект категории"));
        }

        var errors = new List<ValidationError>();
        var input = CatalogRequestSupport.ReadCategory(document.RootElement, errors, out _);
        if (input is null)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        var duplicate = await db.Set<Category>()
            .AsNoTracking()
            .AnyAsync(category => category.Code == input.Code, ct);
        if (duplicate)
        {
            return ProblemResponses.Conflict($"Категория с кодом '{input.Code}' уже существует");
        }

        var entity = new Category
        {
            Id = Guid.NewGuid(),
            Code = input.Code,
            Name = input.Name,
            Icon = input.Icon,
            SortOrder = input.SortOrder,
            IsActive = input.IsActive,
        };

        db.Add(entity);

        // «Сайт» категории: источник, политика и статусы пишутся той же транзакцией.
        await siteSourceProvisioner.EnsureForCategoryAsync(entity.Id, entity.Name, ct);

        await audit.WriteAsync(
            "catalog.category.create",
            EntityType,
            entity.Id.ToString(),
            null,
            CatalogPersistence.ToResponse(entity),
            ct: ct);

        var conflict = await CatalogPersistence.SaveAsync(
            db,
            $"Категория с кодом '{input.Code}' уже существует",
            "Категория используется другими записями каталога",
            ct);
        if (conflict is not null)
        {
            return conflict;
        }

        return Results.Created(
            $"/api/v1/catalog/categories/{entity.Id}",
            CatalogPersistence.ToResponse(entity));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        HttpContext context,
        MayakDbContext db,
        IAuditLog audit,
        ISiteSourceProvisioner siteSourceProvisioner,
        CancellationToken ct)
    {
        using var document = await CatalogRequestSupport.TryReadObjectAsync(context.Request, ct);
        if (document is null)
        {
            return ProblemResponses.Validation(
                new ValidationError("body", "Ожидается JSON-объект категории"));
        }

        var errors = new List<ValidationError>();
        var input = CatalogRequestSupport.ReadCategory(document.RootElement, errors, out _);
        if (input is null)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        var entity = await db.Set<Category>()
            .FirstOrDefaultAsync(category => category.Id == id, ct);
        if (entity is null)
        {
            return ProblemResponses.NotFound($"Категория {id} не существует");
        }

        var duplicate = await db.Set<Category>()
            .AsNoTracking()
            .AnyAsync(category => category.Code == input.Code && category.Id != id, ct);
        if (duplicate)
        {
            return ProblemResponses.Conflict($"Категория с кодом '{input.Code}' уже существует");
        }

        var before = CatalogPersistence.ToResponse(entity);

        entity.Code = input.Code;
        entity.Name = input.Name;
        entity.Icon = input.Icon;
        entity.SortOrder = input.SortOrder;
        entity.IsActive = input.IsActive;

        // Переименование категории меняет название «Сайта»; недостающие статусы добавляются.
        await siteSourceProvisioner.EnsureForCategoryAsync(entity.Id, entity.Name, ct);

        await audit.WriteAsync(
            "catalog.category.update",
            EntityType,
            entity.Id.ToString(),
            before,
            CatalogPersistence.ToResponse(entity),
            ct: ct);

        var conflict = await CatalogPersistence.SaveAsync(
            db,
            $"Категория с кодом '{input.Code}' уже существует",
            "Категория используется другими записями каталога",
            ct);
        if (conflict is not null)
        {
            return conflict;
        }

        return Results.Ok(CatalogPersistence.ToResponse(entity));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        MayakDbContext db,
        IAuditLog audit,
        CancellationToken ct)
    {
        var entity = await db.Set<Category>()
            .FirstOrDefaultAsync(category => category.Id == id, ct);
        if (entity is null)
        {
            return ProblemResponses.NotFound($"Категория {id} не существует");
        }

        var usedByTypes = await db.Set<ObjectType>()
            .AsNoTracking()
            .AnyAsync(objectType => objectType.CategoryId == id, ct);
        var usedByGroups = await db.Set<ObjectGroup>()
            .AsNoTracking()
            .AnyAsync(group => group.CategoryId == id, ct);
        var usedByStatuses = await db.Set<StatusDefinition>()
            .AsNoTracking()
            .AnyAsync(status => status.CategoryId == id, ct);

        if (usedByTypes || usedByGroups || usedByStatuses)
        {
            return ProblemResponses.Conflict(
                "Категория используется типами объектов, группами или статусами и не может быть удалена");
        }

        var before = CatalogPersistence.ToResponse(entity);
        db.Remove(entity);

        await audit.WriteAsync(
            "catalog.category.delete",
            EntityType,
            entity.Id.ToString(),
            before,
            null,
            ct: ct);

        var conflict = await CatalogPersistence.SaveAsync(
            db,
            "Категория используется другими записями каталога",
            "Категория используется другими записями каталога и не может быть удалена",
            ct);
        if (conflict is not null)
        {
            return conflict;
        }

        return Results.NoContent();
    }
}
