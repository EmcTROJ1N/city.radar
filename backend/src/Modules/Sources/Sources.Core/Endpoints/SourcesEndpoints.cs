using BuildingBlocks.Web;

using IdentityAccess.Contracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Sources.Core.Health;
using Sources.Core.Services;

namespace Sources.Core.Endpoints;

/// <summary>
/// <c>/sources</c>: источники и их политики без секретов.
/// Все методы требуют право <c>source.manage</c>; статусы — в <see cref="SourceStatusesEndpoints"/>,
/// подключение — отдельные методы.
/// </summary>
public static class SourcesEndpoints
{
    /// <summary>Ограничение поиска <c>q</c> из контракта.</summary>
    private const int MaxQueryLength = 200;

    /// <summary>Регистрирует эндпоинты источников внутри текущего префикса.</summary>
    public static IEndpointRouteBuilder MapSourcesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/sources");

        group.MapGet(string.Empty, ListAsync)
            .RequireAuthorization(Permissions.SourceManage);
        group.MapGet("/{id:guid}", GetAsync)
            .RequireAuthorization(Permissions.SourceManage);
        group.MapPost(string.Empty, CreateAsync)
            .RequireAuthorization(Permissions.SourceManage);
        group.MapPut("/{id:guid}", UpdateAsync)
            .RequireAuthorization(Permissions.SourceManage);
        group.MapDelete("/{id:guid}", DeleteAsync)
            .RequireAuthorization(Permissions.SourceManage);

        group.MapPost("/{id:guid}/enable", EnableAsync)
            .RequireAuthorization(Permissions.SourceManage);
        group.MapPost("/{id:guid}/disable", DisableAsync)
            .RequireAuthorization(Permissions.SourceManage);

        // Статусы принадлежат источнику: маршруты живут в той же группе.
        group.MapSourceStatusesEndpoints();

        // Health-check источника: состояние, последнее сообщение, детали ошибки.
        group.MapSourceHealthEndpoint();

        return endpoints;
    }

    private static Task<IResult> ListAsync(
        int? limit,
        string? cursor,
        string? q,
        string? sourceType,
        bool? isEnabled,
        SourceService service,
        CancellationToken ct)
    {
        if (q is { Length: > MaxQueryLength })
        {
            return Task.FromResult<IResult>(ProblemResponses.BadRequest(
                $"Параметр q длиннее {MaxQueryLength} символов"));
        }

        if (sourceType is not null
            && sourceType is not ("telegram_chat" or "max_chat"))
        {
            return Task.FromResult<IResult>(ProblemResponses.BadRequest(
                "Неизвестный тип источника: допустимы telegram_chat и max_chat"));
        }

        if (!PageRequest.TryCreate(limit, cursor, out var page, out var failure))
        {
            return Task.FromResult(failure);
        }

        var filter = new SourceFilter(q, sourceType, isEnabled);

        return service.ListAsync(filter, page, ct);
    }

    private static Task<IResult> GetAsync(Guid id, SourceService service, CancellationToken ct) =>
        service.GetAsync(id, ct);

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        SourceService service,
        CancellationToken ct)
    {
        using var document = await SourcesRequestSupport.TryReadObjectAsync(context.Request, ct);
        if (document is null)
        {
            return ProblemResponses.Validation(
                new ValidationError("body", "Ожидается JSON-объект источника"));
        }

        var errors = new List<ValidationError>();
        var input = SourcesRequestSupport.ReadSource(document.RootElement, allowStatuses: true, errors);
        if (input is null)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        return await service.CreateAsync(input, ct);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        HttpContext context,
        SourceService service,
        CancellationToken ct)
    {
        using var document = await SourcesRequestSupport.TryReadObjectAsync(context.Request, ct);
        if (document is null)
        {
            return ProblemResponses.Validation(
                new ValidationError("body", "Ожидается JSON-объект источника"));
        }

        var errors = new List<ValidationError>();
        var input = SourcesRequestSupport.ReadSource(document.RootElement, allowStatuses: false, errors);
        if (input is null)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        return await service.UpdateAsync(id, input, ct);
    }

    private static Task<IResult> DeleteAsync(Guid id, SourceService service, CancellationToken ct) =>
        service.DeleteAsync(id, ct);

    private static Task<IResult> EnableAsync(Guid id, SourceService service, CancellationToken ct) =>
        service.EnableAsync(id, ct);

    private static Task<IResult> DisableAsync(Guid id, SourceService service, CancellationToken ct) =>
        service.DisableAsync(id, ct);
}
