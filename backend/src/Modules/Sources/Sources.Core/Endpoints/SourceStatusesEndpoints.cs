using BuildingBlocks.Web;

using IdentityAccess.Contracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Sources.Core.Services;

namespace Sources.Core.Endpoints;

/// <summary>
/// Статусы источника: <c>GET/POST/PUT/DELETE
/// /sources/{id}/statuses[/{statusId}]</c>. Все методы требуют право <c>source.manage</c>;
/// маршруты добавляются в группу <c>/sources</c> из <see cref="SourcesEndpoints"/>.
/// </summary>
public static class SourceStatusesEndpoints
{
    /// <summary>Регистрирует эндпоинты статусов внутри группы <c>/sources</c>.</summary>
    public static RouteGroupBuilder MapSourceStatusesEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/{id:guid}/statuses", ListAsync)
            .RequireAuthorization(Permissions.SourceManage);
        group.MapPost("/{id:guid}/statuses", CreateAsync)
            .RequireAuthorization(Permissions.SourceManage);
        group.MapPut("/{id:guid}/statuses/{statusId:guid}", UpdateAsync)
            .RequireAuthorization(Permissions.SourceManage);
        group.MapDelete("/{id:guid}/statuses/{statusId:guid}", DeleteAsync)
            .RequireAuthorization(Permissions.SourceManage);

        return group;
    }

    private static Task<IResult> ListAsync(Guid id, SourceStatusService service, CancellationToken ct) =>
        service.ListAsync(id, ct);

    private static async Task<IResult> CreateAsync(
        Guid id,
        HttpContext context,
        SourceStatusService service,
        CancellationToken ct)
    {
        using var document = await SourcesRequestSupport.TryReadObjectAsync(context.Request, ct);
        if (document is null)
        {
            return ProblemResponses.Validation(
                new ValidationError("body", "Ожидается JSON-объект статуса источника"));
        }

        var errors = new List<ValidationError>();
        var input = SourceStatusRequestSupport.Read(
            document.RootElement,
            coreFieldsRequired: !HasStatusDefinition(document.RootElement),
            errors);
        if (input is null)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        return await service.CreateAsync(id, input, ct);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        Guid statusId,
        HttpContext context,
        SourceStatusService service,
        CancellationToken ct)
    {
        using var document = await SourcesRequestSupport.TryReadObjectAsync(context.Request, ct);
        if (document is null)
        {
            return ProblemResponses.Validation(
                new ValidationError("body", "Ожидается JSON-объект статуса источника"));
        }

        var errors = new List<ValidationError>();
        var input = SourceStatusRequestSupport.Read(
            document.RootElement,
            coreFieldsRequired: true,
            errors);
        if (input is null)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        return await service.UpdateAsync(id, statusId, input, ct);
    }

    private static Task<IResult> DeleteAsync(
        Guid id,
        Guid statusId,
        SourceStatusService service,
        CancellationToken ct) =>
        service.DeleteAsync(id, statusId, ct);

    /// <summary>
    /// Задан ли в теле <c>statusDefinitionId</c> строкой: тогда <c>code</c>/<c>name</c>/<c>color</c>/
    /// <c>icon</c> копируются из справочника и в теле необязательны.
    /// </summary>
    private static bool HasStatusDefinition(System.Text.Json.JsonElement root) =>
        root.TryGetProperty("statusDefinitionId", out var value)
        && value.ValueKind == System.Text.Json.JsonValueKind.String;
}
