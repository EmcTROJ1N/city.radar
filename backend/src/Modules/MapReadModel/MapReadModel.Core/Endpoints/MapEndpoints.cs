using System.Globalization;

using BuildingBlocks.Web;
using BuildingBlocks.Web.RateLimiting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MapReadModel.Core.Endpoints;

/// <summary>
/// <c>GET /map/features</c>: публичная выдача фиксаций и их
/// текущего статуса в видимой области. Метод без авторизации — право <c>map.read</c>
/// есть у анонима.
/// </summary>
/// <remarks>
/// Условные запросы: ETag = <c>W/"hash(version + normalizedQuery)"</c>, где version
/// хеш всего состояния карты и каталога, а normalizedQuery — bbox (6 знаков), zoom и
/// отсортированные фильтры. Версия вычисляется до выборки фиксаций, поэтому совпавший
/// <c>If-None-Match</c> даёт 304 без запроса к <c>map_objects</c>/<c>current_statuses</c>.
/// Ответы 200 и 304 содержат ETag и <c>Cache-Control: no-cache</c>: кэш может хранить ответ,
/// но обязан перепроверять его условным запросом. Ответ 400 не получает ETag — у невалидного
/// запроса нет представления.
/// </remarks>
public static class MapEndpoints
{
    /// <summary>Регистрирует эндпоинты карты внутри текущего префикса (например, <c>/api/v1</c>).</summary>
    public static IEndpointRouteBuilder MapMapEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/map");

        group.MapGet("/features", GetFeaturesAsync);
        group.MapGet("/objects/{id:guid}", GetObjectAsync);
        group.MapGet("/objects/{id:guid}/points", GetPointsAsync);

        return endpoints;
    }

    /// <summary>
    /// <c>GET /map/objects/{id}</c>: публичная карточка активной
    /// фиксации. Неактивная и несуществующая фиксация — 404.
    /// </summary>
    private static async Task<IResult> GetObjectAsync(
        Guid id,
        ObjectCardQuery query,
        CancellationToken ct)
    {
        var card = await query.GetAsync(id, ct);

        return card is null
            ? ProblemResponses.NotFound($"Фиксация {id} не существует")
            : Results.Ok(card);
    }

    /// <summary>
    /// <c>GET /map/objects/{id}/points</c>: временные точки по адресам из сообщений зоны.
    /// Публичный; лимит по IP, потому что промах кэша геокодера стоит запроса к Nominatim.
    /// </summary>
    private static async Task<IResult> GetPointsAsync(
        Guid id,
        HttpContext context,
        ZonePointsQuery query,
        IRateLimiter limiter,
        RateLimitOptions options,
        CancellationToken ct)
    {
        if (options.Enabled)
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var result = await limiter.TryAcquireAsync(
                "rl:points:ip:" + ip,
                options.PointsIpLimit,
                options.PointsIpWindow,
                ct);
            if (!result.Allowed)
            {
                context.Response.Headers.RetryAfter =
                    result.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);

                return Results.Problem(
                    detail: "Повторите попытку позже",
                    statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Слишком много запросов",
                    type: "about:blank");
            }
        }

        var points = await query.GetAsync(id, ct);

        return points is null
            ? ProblemResponses.NotFound($"Фиксация {id} не существует")
            : Results.Ok(points);
    }

    private static async Task<IResult> GetFeaturesAsync(
        HttpContext context,
        string? bbox,
        int? zoom,
        string? categories,
        string? statuses,
        string? kinds,
        string? groups,
        MapCache cache,
        MapVersionProvider versionProvider,
        CancellationToken ct)
    {
        if (!BboxParser.TryParse(bbox, out var parsed))
        {
            return ProblemResponses.BadRequest(
                "Параметр bbox должен содержать четыре числа через запятую");
        }

        if (!MapFeaturesQuery.TryCreate(
                parsed,
                zoom,
                categories,
                statuses,
                kinds,
                groups,
                out var query,
                out var error))
        {
            return ProblemResponses.BadRequest(error!);
        }

        // Версия читается из кэша (одна загрузка на секунду) до выборки фиксаций: при
        // совпадении If-None-Match тяжёлый запрос к БД не выполняется вовсе.
        var version = await versionProvider.GetVersionAsync(ct);
        var etag = ETagHelper.CreateWeak(version, query.ToNormalizedString());

        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = ETagHelper.CacheControl;

        var ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
        if (ETagHelper.Matches(ifNoneMatch, etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        // Кэш ответа: ключ включает версию, поэтому 304 выше уже отсекает
        // неизменённые данные, а попадание в кэш не требует выборки из БД.
        var response = await cache.GetAsync(query, version, ct);

        return Results.Ok(response);
    }
}
