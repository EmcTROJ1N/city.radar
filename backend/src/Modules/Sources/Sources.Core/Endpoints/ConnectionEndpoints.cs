using BuildingBlocks.Web;

using IdentityAccess.Contracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Sources.Core.Secrets;
using Sources.Core.Services;

namespace Sources.Core.Endpoints;

/// <summary>
/// <c>GET/PUT /admin/sources/{id}/connection</c>: просмотр и
/// назначение Telegram-сессии и прокси источнику. Значения секретов не возвращаются — только
/// идентификаторы. Право — <c>source.secrets.manage</c>.
/// </summary>
public static class ConnectionEndpoints
{
    /// <summary>Регистрирует подключение источников внутри текущего префикса.</summary>
    public static IEndpointRouteBuilder MapConnectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints
            .MapGroup("/admin/sources")
            .RequireAuthorization(Permissions.SourceSecretsManage);

        group.MapGet("/{id:guid}/connection", GetAsync);
        group.MapPut("/{id:guid}/connection", UpdateAsync);

        return endpoints;
    }

    private static Task<IResult> GetAsync(
        Guid id,
        ConnectionService service,
        CancellationToken ct) =>
        service.GetAsync(id, ct);

    private static async Task<IResult> UpdateAsync(
        Guid id,
        HttpContext context,
        ConnectionService service,
        CancellationToken ct)
    {
        using var document = await SourcesRequestSupport.TryReadObjectAsync(context.Request, ct);
        if (document is null)
        {
            return ProblemResponses.Validation(
                new ValidationError("body", "Ожидается JSON-объект подключения источника"));
        }

        var errors = new List<ValidationError>();
        var input = SecretsRequestSupport.ReadConnection(document.RootElement, errors);
        if (input is null)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        return await service.UpdateAsync(id, input, ct);
    }
}
