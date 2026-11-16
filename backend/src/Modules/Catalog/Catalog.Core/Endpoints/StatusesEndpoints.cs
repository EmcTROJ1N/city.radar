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
/// <c>/catalog/statuses</c>: справочник статусов (библиотека стилей).
/// Чтение публичное, запись — по праву <c>catalog.status.manage</c>. Цвет — только ключ
/// палитры <c>ok|limited|none|queue|schedule|stale</c>.
/// </summary>
public static class StatusesEndpoints
{
    private const string EntityType = "status_definition";
    private const string CategoryField = "categoryId";

    /// <summary>Регистрирует эндпоинты справочника статусов внутри текущего префикса.</summary>
    public static IEndpointRouteBuilder MapStatusesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/catalog/statuses");

        // Чтение публичное: в контракте у GET security: [] — анонимам разрешено.
        group.MapGet(string.Empty, ListAsync);

        group.MapPost(string.Empty, CreateAsync)
            .RequireAuthorization(Permissions.CatalogStatusManage);
        group.MapPut("/{id:guid}", UpdateAsync)
            .RequireAuthorization(Permissions.CatalogStatusManage);
        group.MapDelete("/{id:guid}", DeleteAsync)
            .RequireAuthorization(Permissions.CatalogStatusManage);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(MayakDbContext db, CancellationToken ct)
    {
        var statuses = await db.Set<StatusDefinition>()
            .AsNoTracking()
            .OrderBy(status => status.Severity)
            .ThenBy(status => status.Name)
            .ToListAsync(ct);

        return Results.Ok(statuses.Select(ToResponse).ToArray());
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
                new ValidationError("body", "Ожидается JSON-объект статуса"));
        }

        var errors = new List<ValidationError>();
        var input = CatalogRequestSupport.ReadStatusDefinition(document.RootElement, errors);
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

        var duplicate = await db.Set<StatusDefinition>()
            .AsNoTracking()
            .AnyAsync(status => status.Code == input.Code, ct);
        if (duplicate)
        {
            return ProblemResponses.Conflict($"Статус с кодом '{input.Code}' уже существует");
        }

        var entity = new StatusDefinition
        {
            Id = Guid.NewGuid(),
            CategoryId = input.CategoryId,
            Code = input.Code,
            Name = input.Name,
            Color = input.Color,
            Icon = input.Icon,
            Severity = input.Severity,
            IsPositive = input.IsPositive,
        };

        db.Add(entity);

        await audit.WriteAsync(
            "catalog.status.create",
            EntityType,
            entity.Id.ToString(),
            null,
            ToResponse(entity),
            ct: ct);

        var conflict = await CatalogPersistence.SaveAsync(
            db,
            $"Статус с кодом '{input.Code}' уже существует",
            "Статус используется и не может быть изменён",
            ct);
        if (conflict is not null)
        {
            return conflict;
        }

        return Results.Created($"/api/v1/catalog/statuses/{entity.Id}", ToResponse(entity));
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
                new ValidationError("body", "Ожидается JSON-объект статуса"));
        }

        var errors = new List<ValidationError>();
        var input = CatalogRequestSupport.ReadStatusDefinition(document.RootElement, errors);
        if (input is null)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        var entity = await db.Set<StatusDefinition>()
            .FirstOrDefaultAsync(status => status.Id == id, ct);
        if (entity is null)
        {
            return ProblemResponses.NotFound($"Статус {id} не существует");
        }

        if (input.CategoryId is { } categoryId
            && !await CatalogPersistence.CategoryExistsAsync(db, categoryId, ct))
        {
            return ProblemResponses.Validation(
                new ValidationError(CategoryField, $"Категория {categoryId} не существует"));
        }

        var duplicate = await db.Set<StatusDefinition>()
            .AsNoTracking()
            .AnyAsync(status => status.Code == input.Code && status.Id != id, ct);
        if (duplicate)
        {
            return ProblemResponses.Conflict($"Статус с кодом '{input.Code}' уже существует");
        }

        var before = ToResponse(entity);

        entity.CategoryId = input.CategoryId;
        entity.Code = input.Code;
        entity.Name = input.Name;
        entity.Color = input.Color;
        entity.Icon = input.Icon;
        entity.Severity = input.Severity;
        entity.IsPositive = input.IsPositive;

        await audit.WriteAsync(
            "catalog.status.update",
            EntityType,
            entity.Id.ToString(),
            before,
            ToResponse(entity),
            ct: ct);

        var conflict = await CatalogPersistence.SaveAsync(
            db,
            $"Статус с кодом '{input.Code}' уже существует",
            "Статус используется и не может быть изменён",
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
        var entity = await db.Set<StatusDefinition>()
            .FirstOrDefaultAsync(status => status.Id == id, ct);
        if (entity is null)
        {
            return ProblemResponses.NotFound($"Статус {id} не существует");
        }

        var before = ToResponse(entity);
        db.Remove(entity);

        await audit.WriteAsync(
            "catalog.status.delete",
            EntityType,
            entity.Id.ToString(),
            before,
            null,
            ct: ct);

        // Ссылки из source_statuses появятся вместе с таблицей статусов источника: тогда
        // нарушение FK станет 409.
        var conflict = await CatalogPersistence.SaveAsync(
            db,
            "Статус уже существует",
            "Статус используется статусами источников и не может быть удалён",
            ct);
        if (conflict is not null)
        {
            return conflict;
        }

        return Results.NoContent();
    }

    private static StatusDefinitionResponse ToResponse(StatusDefinition status) =>
        new(
            status.Id,
            status.CategoryId,
            status.Code,
            status.Name,
            status.Color,
            status.Icon,
            status.Severity,
            status.IsPositive);
}

/// <summary>Элемент ответа по схеме <c>StatusDefinition</c>.</summary>
internal sealed record StatusDefinitionResponse(
    Guid Id,
    Guid? CategoryId,
    string Code,
    string Name,
    string Color,
    string Icon,
    int Severity,
    bool IsPositive);
