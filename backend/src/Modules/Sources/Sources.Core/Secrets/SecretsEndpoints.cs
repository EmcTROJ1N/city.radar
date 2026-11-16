using BuildingBlocks.Web;

using IdentityAccess.Contracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Sources.Core.Endpoints;

namespace Sources.Core.Secrets;

/// <summary>
/// Админские методы секретов: запись сессий,
/// прокси и единственного секрета LLM, список метаданных. Все методы — write-only для значений:
/// <c>GET /admin/secrets</c> отдаёт только вид, название, наличие значения и время изменения.
/// Право — <c>source.secrets.manage</c> (модератору недоступно).
/// </summary>
public static class SecretsEndpoints
{
    /// <summary>Регистрирует <c>/admin/secrets</c> внутри текущего префикса (<c>/api/v1</c>).</summary>
    public static IEndpointRouteBuilder MapSecretsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints
            .MapGroup("/admin/secrets")
            .RequireAuthorization(Permissions.SourceSecretsManage);

        group.MapGet(string.Empty, ListAsync);
        group.MapPut("/telegram-sessions/{id:guid}", PutTelegramSessionAsync);
        group.MapPut("/proxies/{id:guid}", PutProxyAsync);
        group.MapPut("/llm", PutLlmAsync);

        return endpoints;
    }

    private static Task<IResult> ListAsync(SecretStore store, CancellationToken ct) =>
        store.ListAsync(ct);

    private static async Task<IResult> PutTelegramSessionAsync(
        Guid id,
        HttpContext context,
        SecretStore store,
        CancellationToken ct)
    {
        using var document = await SourcesRequestSupport.TryReadObjectAsync(context.Request, ct);
        if (document is null)
        {
            return ProblemResponses.Validation(
                new ValidationError("body", "Ожидается JSON-объект секрета Telegram-сессии"));
        }

        var errors = new List<ValidationError>();
        var input = SecretsRequestSupport.ReadTelegramSession(document.RootElement, errors);
        if (input is null)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        return await store.PutTelegramSessionAsync(id, input, ct);
    }

    private static async Task<IResult> PutProxyAsync(
        Guid id,
        HttpContext context,
        SecretStore store,
        CancellationToken ct)
    {
        using var document = await SourcesRequestSupport.TryReadObjectAsync(context.Request, ct);
        if (document is null)
        {
            return ProblemResponses.Validation(
                new ValidationError("body", "Ожидается JSON-объект прокси"));
        }

        var errors = new List<ValidationError>();
        var input = SecretsRequestSupport.ReadProxy(document.RootElement, errors);
        if (input is null)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        return await store.PutProxyAsync(id, input, ct);
    }

    private static async Task<IResult> PutLlmAsync(
        HttpContext context,
        SecretStore store,
        CancellationToken ct)
    {
        using var document = await SourcesRequestSupport.TryReadObjectAsync(context.Request, ct);
        if (document is null)
        {
            return ProblemResponses.Validation(
                new ValidationError("body", "Ожидается JSON-объект настроек LLM"));
        }

        var errors = new List<ValidationError>();
        var input = SecretsRequestSupport.ReadLlm(document.RootElement, errors);
        if (input is null)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        return await store.PutLlmAsync(input, ct);
    }
}
