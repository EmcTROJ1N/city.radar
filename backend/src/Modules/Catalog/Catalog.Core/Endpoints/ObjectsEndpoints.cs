using BuildingBlocks.Web;

using Catalog.Core.Services;

using IdentityAccess.Contracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Catalog.Core.Endpoints;

/// <summary>
/// <c>/catalog/objects</c>: фиксации на карте в редакторском
/// виде, включая неактивные. Объекты <b>не публичны</b>: у всех методов контракта
/// <c>x-permission: catalog.object.manage</c>. Публичная выдача карты — <c>/map/features</c>.
/// </summary>
public static class ObjectsEndpoints
{
    /// <summary>Ограничение поиска <c>q</c> из контракта (<c>ObjectSearch.maxLength</c>).</summary>
    private const int MaxQueryLength = 200;

    /// <summary>Регистрирует эндпоинты фиксаций внутри текущего префикса.</summary>
    public static IEndpointRouteBuilder MapObjectsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/catalog/objects");

        group.MapGet(string.Empty, ListAsync)
            .RequireAuthorization(Permissions.CatalogObjectManage);
        group.MapGet("/{id:guid}", GetAsync)
            .RequireAuthorization(Permissions.CatalogObjectManage);
        group.MapPost(string.Empty, CreateAsync)
            .RequireAuthorization(Permissions.CatalogObjectManage);
        group.MapPut("/{id:guid}", UpdateAsync)
            .RequireAuthorization(Permissions.CatalogObjectManage);
        group.MapDelete("/{id:guid}", DeleteAsync)
            .RequireAuthorization(Permissions.CatalogObjectManage);

        // Привязка и отвязка источников: у фиксации может быть несколько источников.
        group.MapPut("/{id:guid}/sources/{sourceId:guid}", LinkSourceAsync)
            .RequireAuthorization(Permissions.CatalogObjectManage);
        group.MapDelete("/{id:guid}/sources/{sourceId:guid}", UnlinkSourceAsync)
            .RequireAuthorization(Permissions.CatalogObjectManage);

        return endpoints;
    }

    private static Task<IResult> ListAsync(
        int? limit,
        string? cursor,
        Guid? sourceId,
        Guid? groupId,
        Guid? categoryId,
        Guid? objectTypeId,
        string? kind,
        bool? isActive,
        string? q,
        MapObjectService service,
        CancellationToken ct)
    {
        if (q is { Length: > MaxQueryLength })
        {
            return Task.FromResult<IResult>(ProblemResponses.BadRequest(
                $"Параметр q длиннее {MaxQueryLength} символов"));
        }

        if (!PageRequest.TryCreate(limit, cursor, out var page, out var failure))
        {
            return Task.FromResult(failure);
        }

        var filter = new MapObjectFilter(sourceId, groupId, categoryId, objectTypeId, kind, isActive, q);

        return service.ListAsync(filter, page, ct);
    }

    private static Task<IResult> GetAsync(Guid id, MapObjectService service, CancellationToken ct) =>
        service.GetAsync(id, ct);

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        MapObjectService service,
        CancellationToken ct)
    {
        using var document = await CatalogRequestSupport.TryReadObjectAsync(context.Request, ct);
        if (document is null)
        {
            return ProblemResponses.Validation(
                new ValidationError("body", "Ожидается JSON-объект фиксации"));
        }

        var errors = new List<ValidationError>();
        var input = CatalogRequestSupport.ReadMapObject(document.RootElement, errors);
        if (input is null)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        return await service.CreateAsync(input, ct);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        HttpContext context,
        MapObjectService service,
        CancellationToken ct)
    {
        using var document = await CatalogRequestSupport.TryReadObjectAsync(context.Request, ct);
        if (document is null)
        {
            return ProblemResponses.Validation(
                new ValidationError("body", "Ожидается JSON-объект фиксации"));
        }

        var errors = new List<ValidationError>();
        var input = CatalogRequestSupport.ReadMapObject(document.RootElement, errors);
        if (input is null)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        return await service.UpdateAsync(id, input, ct);
    }

    private static Task<IResult> DeleteAsync(Guid id, MapObjectService service, CancellationToken ct) =>
        service.DeleteAsync(id, ct);

    private static Task<IResult> LinkSourceAsync(
        Guid id,
        Guid sourceId,
        MapObjectService service,
        CancellationToken ct) =>
        service.LinkSourceAsync(id, sourceId, ct);

    private static Task<IResult> UnlinkSourceAsync(
        Guid id,
        Guid sourceId,
        MapObjectService service,
        CancellationToken ct) =>
        service.UnlinkSourceAsync(id, sourceId, ct);
}
