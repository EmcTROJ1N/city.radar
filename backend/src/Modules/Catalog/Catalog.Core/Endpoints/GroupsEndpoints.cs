using BuildingBlocks.Persistence;
using BuildingBlocks.Web;

using Catalog.Core.Domain;

using IdentityAccess.Contracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Core.Endpoints;

/// <summary>
/// <c>/catalog/groups</c>: группы объектов для фильтра карты. Чтение
/// публичное, запись — по праву <c>catalog.group.manage</c>. Имя уникально без учёта регистра
/// (индекс по <c>lower(name)</c>), удаление группы разрешено: у фиксаций <c>group_id</c>
/// станет <c>null</c> (FK <c>on delete set null</c> будущей таблицы).
/// </summary>
public static class GroupsEndpoints
{
    private const string EntityType = "object_group";
    private const string CategoryField = "categoryId";

    /// <summary>Регистрирует эндпоинты групп внутри текущего префикса.</summary>
    public static IEndpointRouteBuilder MapGroupsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/catalog/groups");

        // Чтение публичное: в контракте у GET security: [] — анонимам разрешено.
        group.MapGet(string.Empty, ListAsync);

        group.MapPost(string.Empty, CreateAsync)
            .RequireAuthorization(Permissions.CatalogGroupManage);
        group.MapPut("/{id:guid}", UpdateAsync)
            .RequireAuthorization(Permissions.CatalogGroupManage);
        group.MapDelete("/{id:guid}", DeleteAsync)
            .RequireAuthorization(Permissions.CatalogGroupManage);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(MayakDbContext db, CancellationToken ct)
    {
        var groups = await db.Set<ObjectGroup>()
            .AsNoTracking()
            .OrderBy(group => group.Name)
            .ToListAsync(ct);

        return Results.Ok(groups.Select(ToResponse).ToArray());
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        MayakDbContext db,
        IAuditLog audit,
        CancellationToken ct)
    {
        using var document = await CatalogRequestSupport.TryReadObjectAsync(context.Request, ct);
        if (document is null)
        {
            return ProblemResponses.Validation(
                new ValidationError("body", "Ожидается JSON-объект группы"));
        }

        var errors = new List<ValidationError>();
        var input = CatalogRequestSupport.ReadObjectGroup(document.RootElement, errors);
        if (input is null)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        if (input.CategoryId is { } categoryId
            && !await CatalogPersistence.CategoryExistsAsync(db, categoryId, ct))
        {
            return ProblemResponses.Validation(
                new ValidationError(CategoryField, $"Категория {categoryId} не существует"));
        }

        if (await NameExistsAsync(db, input.Name, exceptId: null, ct))
        {
            return ProblemResponses.Conflict($"Группа с названием '{input.Name}' уже существует");
        }

        var entity = new ObjectGroup
        {
            Id = Guid.NewGuid(),
            CategoryId = input.CategoryId,
            Name = input.Name,
            Color = input.Color,
            IsVisibleByDefault = input.IsVisibleByDefault,
        };

        db.Add(entity);

        await audit.WriteAsync(
            "catalog.group.create",
            EntityType,
            entity.Id.ToString(),
            null,
            ToResponse(entity),
            ct: ct);

        var conflict = await CatalogPersistence.SaveAsync(
            db,
            $"Группа с названием '{input.Name}' уже существует",
            "Группа используется фиксациями и не может быть изменена",
            ct);
        if (conflict is not null)
        {
            return conflict;
        }

        return Results.Created($"/api/v1/catalog/groups/{entity.Id}", ToResponse(entity));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        HttpContext context,
        MayakDbContext db,
        IAuditLog audit,
        CancellationToken ct)
    {
        using var document = await CatalogRequestSupport.TryReadObjectAsync(context.Request, ct);
        if (document is null)
        {
            return ProblemResponses.Validation(
                new ValidationError("body", "Ожидается JSON-объект группы"));
        }

        var errors = new List<ValidationError>();
        var input = CatalogRequestSupport.ReadObjectGroup(document.RootElement, errors);
        if (input is null)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        var entity = await db.Set<ObjectGroup>()
            .FirstOrDefaultAsync(group => group.Id == id, ct);
        if (entity is null)
        {
            return ProblemResponses.NotFound($"Группа {id} не существует");
        }

        if (input.CategoryId is { } categoryId
            && !await CatalogPersistence.CategoryExistsAsync(db, categoryId, ct))
        {
            return ProblemResponses.Validation(
                new ValidationError(CategoryField, $"Категория {categoryId} не существует"));
        }

        if (await NameExistsAsync(db, input.Name, exceptId: id, ct))
        {
            return ProblemResponses.Conflict($"Группа с названием '{input.Name}' уже существует");
        }

        var before = ToResponse(entity);

        entity.CategoryId = input.CategoryId;
        entity.Name = input.Name;
        entity.Color = input.Color;
        entity.IsVisibleByDefault = input.IsVisibleByDefault;

        await audit.WriteAsync(
            "catalog.group.update",
            EntityType,
            entity.Id.ToString(),
            before,
            ToResponse(entity),
            ct: ct);

        var conflict = await CatalogPersistence.SaveAsync(
            db,
            $"Группа с названием '{input.Name}' уже существует",
            "Группа используется фиксациями и не может быть изменена",
            ct);
        if (conflict is not null)
        {
            return conflict;
        }

        return Results.Ok(ToResponse(entity));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        MayakDbContext db,
        IAuditLog audit,
        CancellationToken ct)
    {
        var entity = await db.Set<ObjectGroup>()
            .FirstOrDefaultAsync(group => group.Id == id, ct);
        if (entity is null)
        {
            return ProblemResponses.NotFound($"Группа {id} не существует");
        }

        var before = ToResponse(entity);
        db.Remove(entity);

        await audit.WriteAsync(
            "catalog.group.delete",
            EntityType,
            entity.Id.ToString(),
            before,
            null,
            ct: ct);

        var conflict = await CatalogPersistence.SaveAsync(
            db,
            "Группа уже существует",
            "Группа используется фиксациями и не может быть удалена",
            ct);
        if (conflict is not null)
        {
            return conflict;
        }

        return Results.NoContent();
    }

    /// <summary>
    /// Дубль имени без учёта регистра: <c>lower(name)</c> — тот же предикат, что и в
    /// уникальном индексе <c>object_groups_name_uq</c>.
    /// </summary>
    private static Task<bool> NameExistsAsync(
        MayakDbContext db,
        string name,
        Guid? exceptId,
        CancellationToken ct)
    {
        var normalized = name.ToLowerInvariant();

        return db.Set<ObjectGroup>()
            .AsNoTracking()
            .AnyAsync(
                group => group.Name.ToLower() == normalized
                    && (exceptId == null || group.Id != exceptId),
                ct);
    }

    private static ObjectGroupResponse ToResponse(ObjectGroup group) =>
        new(group.Id, group.CategoryId, group.Name, group.Color, group.IsVisibleByDefault);
}

/// <summary>Элемент ответа по схеме <c>ObjectGroup</c>.</summary>
internal sealed record ObjectGroupResponse(
    Guid Id,
    Guid? CategoryId,
    string Name,
    string? Color,
    bool IsVisibleByDefault);
