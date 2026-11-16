using System.Globalization;
using System.Text.Json;

using BuildingBlocks.Web.RateLimiting;

using IdentityAccess.Contracts;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Observations.Infrastructure;

/// <summary>
/// Rate limit ручной отметки: три независимых скользящих окна
/// на пользователя, на IP и на фиксацию. Превышение любого даёт 429
/// <c>application/problem+json</c> с заголовком <c>Retry-After</c> в секундах.
/// </summary>
/// <remarks>
/// Фильтр применяется к <c>POST /observations</c> в точке подключения маршрутов
/// (<see cref="ObservationsEndpoints"/>), поэтому ни обработчик отметки, ни модуль карты о
/// лимитере не знают. Ключи: <c>rl:obs:user:{id}</c>, <c>rl:obs:ip:{ip}</c>,
/// <c>rl:obs:obj:{id}</c>. Лимит на пользователя — <c>global_settings.observation_rate_per_hour</c>
/// с часовым окном; если настройки прочитать нельзя, действуют резервные константы
/// <see cref="RateLimitOptions.ObservationUserFallbackLimit"/> (10/мин). Остальные лимиты — из
/// <see cref="RateLimitOptions"/> (30/мин на IP и 60/мин на фиксацию).
/// <para>
/// Отклонённый запрос не читает БД и не создаёт наблюдение. При недоступном Redis счётчики
/// ведёт лимитер в памяти процесса: 429 остаётся честным, а не превращается в 500.
/// </para>
/// </remarks>
public sealed class ObservationRateLimitFilter(
    IRateLimiter limiter,
    ICurrentUser currentUser,
    RateLimitOptions options,
    IGlobalSettings? settings = null,
    ILogger<ObservationRateLimitFilter>? logger = null) : IEndpointFilter
{
    /// <summary>Префикс ключа лимита на пользователя.</summary>
    public const string UserKeyPrefix = "rl:obs:user:";

    /// <summary>Префикс ключа лимита на IP.</summary>
    public const string IpKeyPrefix = "rl:obs:ip:";

    /// <summary>Префикс ключа лимита на фиксацию.</summary>
    public const string ObjectKeyPrefix = "rl:obs:obj:";

    /// <summary>Подставляется в ключ, если IP клиента неизвестен (например, в TestServer).</summary>
    public const string UnknownIp = "unknown";

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (!options.Enabled)
        {
            return await next(context).ConfigureAwait(false);
        }

        var http = context.HttpContext;
        var ct = http.RequestAborted;

        if (currentUser.UserId is { } userId)
        {
            var (userLimit, userWindow) = await ResolveUserLimitAsync(ct).ConfigureAwait(false);
            var userResult = await limiter
                .TryAcquireAsync(
                    UserKeyPrefix + userId.ToString("D"),
                    userLimit,
                    userWindow,
                    ct)
                .ConfigureAwait(false);

            if (!userResult.Allowed)
            {
                return TooManyRequests(http, userResult);
            }
        }

        var ip = string.IsNullOrWhiteSpace(currentUser.Ip) ? UnknownIp : currentUser.Ip;
        var ipResult = await limiter
            .TryAcquireAsync(
                IpKeyPrefix + ip,
                options.ObservationIpLimit,
                options.ObservationIpWindow,
                ct)
            .ConfigureAwait(false);

        if (!ipResult.Allowed)
        {
            return TooManyRequests(http, ipResult);
        }

        var mapObjectId = await ReadMapObjectIdAsync(http.Request, ct).ConfigureAwait(false);
        if (mapObjectId is { } objectId)
        {
            var objectResult = await limiter
                .TryAcquireAsync(
                    ObjectKeyPrefix + objectId.ToString("D"),
                    options.ObservationObjectLimit,
                    options.ObservationObjectWindow,
                    ct)
                .ConfigureAwait(false);

            if (!objectResult.Allowed)
            {
                return TooManyRequests(http, objectResult);
            }
        }

        return await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Лимит на пользователя и его окно: из <c>global_settings</c>, а если настройки прочитать
    /// нельзя (сервис не зарегистрирован или запрос упал) — резервные константы 10/мин.
    /// Сбой чтения настроек не должен ронять отметку: лимит остаётся, запрос обрабатывается.
    /// </summary>
    private async Task<(int Limit, TimeSpan Window)> ResolveUserLimitAsync(CancellationToken ct)
    {
        if (settings is null)
        {
            return (options.ObservationUserFallbackLimit, options.ObservationUserFallbackWindow);
        }

        try
        {
            var snapshot = await settings.GetAsync(ct).ConfigureAwait(false);

            return (Math.Max(1, snapshot.ObservationRatePerHour), options.ObservationUserWindow);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger?.LogWarning(
                exception,
                "Не удалось прочитать глобальные настройки; лимит на пользователя {Limit}/мин",
                options.ObservationUserFallbackLimit);

            return (options.ObservationUserFallbackLimit, options.ObservationUserFallbackWindow);
        }
    }

    private static IResult TooManyRequests(HttpContext http, RateLimitResult result)
    {
        http.Response.Headers.RetryAfter =
            result.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        return Results.Problem(
            detail: "Повторите попытку позже",
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Слишком много запросов",
            type: "about:blank");
    }

    /// <summary>
    /// Читает <c>mapObjectId</c> из тела, не мешая обработчику: поток буферизуется и
    /// возвращается в начало. Невалидное тело — не забота лимитера: обработчик ответит 422.
    /// </summary>
    private static async Task<Guid?> ReadMapObjectIdAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength == 0)
        {
            return null;
        }

        request.EnableBuffering();

        try
        {
            using var document = await JsonDocument
                .ParseAsync(request.Body, cancellationToken: ct)
                .ConfigureAwait(false);

            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("mapObjectId", out var value)
                && value.ValueKind == JsonValueKind.String
                && Guid.TryParse(value.GetString(), out var objectId))
            {
                return objectId;
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            request.Body.Position = 0;
        }
    }
}
