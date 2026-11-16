using System.Text.Json;

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
/// <c>/catalog/object-types</c>: типы объектов с JSON Schema атрибутов.
/// Чтение публичное, запись — по праву <c>catalog.objecttype.manage</c>. Схема проверяется
/// пакетом JsonSchema.Net, тем же, которым будут валидироваться атрибуты фиксаций.
/// </summary>
public static class ObjectTypesEndpoints
{
    private const string EntityType = "object_type";
    private const string CategoryField = "categoryId";

    /// <summary>Регистрирует эндпоинты типов объектов внутри текущего префикса.</summary>
    public static IEndpointRouteBuilder MapObjectTypesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/catalog/object-types");

        // Чтение публичное: в контракте у GET security: [] — анонимам разрешено.
        group.MapGet(string.Empty, ListAsync);

        group.MapPost(string.Empty, CreateAsync)
            .RequireAuthorization(Permissions.CatalogObjectTypeManage);
        group.MapPut("/{id:guid}", UpdateAsync)
            .RequireAuthorization(Permissions.CatalogObjectTypeManage);
        group.MapDelete("/{id:guid}", DeleteAsync)
            .RequireAuthorization(Permissions.CatalogObjectTypeManage);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        Guid? categoryId,
        MayakDbContext db,
        CancellationToken ct)
    {
        var query = db.Set<ObjectType>().AsNoTracking();

        if (categoryId is { } filter)
        {
            query = query.Where(objectType => objectType.CategoryId == filter);
        }

        var objectTypes = await query
            .OrderBy(objectType => objectType.Name)
            .ToListAsync(ct);

        return Results.Ok(objectTypes.Select(ToResponse).ToArray());
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
                new ValidationError("body", "Ожидается JSON-объект типа объекта"));
        }

        var errors = new List<ValidationError>();
        var input = CatalogRequestSupport.ReadObjectType(document.RootElement, errors, out _);
        if (input is null)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        if (!await CatalogPersistence.CategoryExistsAsync(db, input.CategoryId, ct))
        {
            return ProblemResponses.Validation(
                new ValidationError(CategoryField, $"Категория {input.CategoryId} не существует"));
        }

        var duplicate = await db.Set<ObjectType>()
            .AsNoTracking()
            .AnyAsync(objectType => objectType.Code == input.Code, ct);
        if (duplicate)
        {
            return ProblemResponses.Conflict($"Тип объекта с кодом '{input.Code}' уже существует");
        }

        var entity = new ObjectType
        {
            Id = Guid.NewGuid(),
            CategoryId = input.CategoryId,
            Code = input.Code,
            Name = input.Name,
            AttributesSchema = input.AttributesSchema,
            GeometryKind = input.GeometryKind,
        };

        db.Add(entity);

        await audit.WriteAsync(
            "catalog.objecttype.create",
            EntityType,
            entity.Id.ToString(),
            null,
            ToResponse(entity),
            ct: ct);

        var conflict = await CatalogPersistence.SaveAsync(
            db,
            $"Тип объекта с кодом '{input.Code}' уже существует",
            "Тип объекта используется фиксациями и не может быть изменён",
            ct);
        if (conflict is not null)
        {
            return conflict;
        }

        return Results.Created($"/api/v1/catalog/object-types/{entity.Id}", ToResponse(entity));
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
                new ValidationError("body", "Ожидается JSON-объект типа объекта"));
        }

        var errors = new List<ValidationError>();
        var input = CatalogRequestSupport.ReadObjectType(document.RootElement, errors, out _);
        if (input is null)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        var entity = await db.Set<ObjectType>()
            .FirstOrDefaultAsync(objectType => objectType.Id == id, ct);
        if (entity is null)
        {
            return ProblemResponses.NotFound($"Тип объекта {id} не существует");
        }

        if (!await CatalogPersistence.CategoryExistsAsync(db, input.CategoryId, ct))
        {
            return ProblemResponses.Validation(
                new ValidationError(CategoryField, $"Категория {input.CategoryId} не существует"));
        }

        var duplicate = await db.Set<ObjectType>()
            .AsNoTracking()
            .AnyAsync(objectType => objectType.Code == input.Code && objectType.Id != id, ct);
        if (duplicate)
        {
            return ProblemResponses.Conflict($"Тип объекта с кодом '{input.Code}' уже существует");
        }

        var before = ToResponse(entity);

        entity.CategoryId = input.CategoryId;
        entity.Code = input.Code;
        entity.Name = input.Name;
        entity.AttributesSchema = input.AttributesSchema;
        entity.GeometryKind = input.GeometryKind;

        await audit.WriteAsync(
            "catalog.objecttype.update",
            EntityType,
            entity.Id.ToString(),
            before,
            ToResponse(entity),
            ct: ct);

        var conflict = await CatalogPersistence.SaveAsync(
            db,
            $"Тип объекта с кодом '{input.Code}' уже существует",
            "Тип объекта используется фиксациями и не может быть изменён",
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
        var entity = await db.Set<ObjectType>()
            .FirstOrDefaultAsync(objectType => objectType.Id == id, ct);
        if (entity is null)
        {
            return ProblemResponses.NotFound($"Тип объекта {id} не существует");
        }

        var before = ToResponse(entity);
        db.Remove(entity);

        await audit.WriteAsync(
            "catalog.objecttype.delete",
            EntityType,
            entity.Id.ToString(),
            before,
            null,
            ct: ct);

        // Ссылки из map_objects появятся вместе с таблицей фиксаций: тогда нарушение FK
        // внешнего ключа станет 409, а не 500.
        var conflict = await CatalogPersistence.SaveAsync(
            db,
            "Тип объекта уже существует",
            "Тип объекта используется фиксациями и не может быть удалён",
            ct);
        if (conflict is not null)
        {
            return conflict;
        }

        return Results.NoContent();
    }

    private static ObjectTypeResponse ToResponse(ObjectType objectType) =>
        new(
            objectType.Id,
            objectType.CategoryId,
            objectType.Code,
            objectType.Name,
            ParseSchema(objectType.AttributesSchema),
            objectType.GeometryKind);

    /// <summary>jsonb из БД отдаётся в ответе объектом, а не строкой.</summary>
    private static JsonElement ParseSchema(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }
}

/// <summary>Элемент ответа по схеме <c>ObjectType</c>.</summary>
internal sealed record ObjectTypeResponse(
    Guid Id,
    Guid CategoryId,
    string Code,
    string Name,
    JsonElement AttributesSchema,
    string GeometryKind);
