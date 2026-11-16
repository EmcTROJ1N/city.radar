using IdentityAccess.Contracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

namespace IdentityAccess.Core.Auth;

/// <summary>
/// Эндпоинты Identity &amp; Access: заглушка входа через MAX, refresh с защитой от CSRF,
/// выход и профиль <c>/me</c>. Подключаются внутри префикса <c>/api/v1</c>.
/// </summary>
public static class AuthEndpoints
{
    /// <summary>Имя httpOnly-cookie с refresh-токеном.</summary>
    public const string RefreshCookieName = "refresh_token";

    /// <summary>Заголовок CSRF-защиты refresh: значение должно быть <see cref="CsrfHeaderValue"/>.</summary>
    public const string CsrfHeaderName = "X-Requested-With";

    /// <summary>Обязательное значение <see cref="CsrfHeaderName"/>.</summary>
    public const string CsrfHeaderValue = "mayak";

    /// <summary>Путь cookie: refresh и logout живут под <c>/auth</c>.</summary>
    private const string RefreshCookiePath = "/api/v1/auth";

    private const string AllowedOriginsSection = "Cors:AllowedOrigins";

    /// <summary>Регистрирует <c>/auth/max</c>, <c>/auth/refresh</c>, <c>/auth/logout</c> и <c>/me</c>.</summary>
    public static IEndpointRouteBuilder MapIdentityAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/auth/max", LoginByMax);
        endpoints.MapPost("/auth/refresh", RefreshAsync);
        endpoints.MapPost("/auth/logout", LogoutAsync).RequireAuthorization();
        endpoints.MapGet("/me", GetMeAsync).RequireAuthorization();

        return endpoints;
    }

    /// <summary>Вход через MAX ещё не реализован, метод всегда отвечает 501.</summary>
    private static IResult LoginByMax() =>
        Results.Problem(
            statusCode: StatusCodes.Status501NotImplemented,
            title: "Не реализовано",
            detail: "Авторизация будет доступна позже",
            type: "about:blank");

    /// <summary>
    /// Ротация refresh-токена. Требует cookie и CSRF-заголовок; при наличии <c>Origin</c> он
    /// должен совпадать с хостом запроса или с <c>Cors:AllowedOrigins</c>.
    /// </summary>
    private static async Task<IResult> RefreshAsync(
        HttpContext context,
        RefreshTokenService refreshTokens,
        TokenService tokenService,
        UserProfileService profiles,
        IConfiguration configuration,
        CancellationToken ct)
    {
        if (!HasCsrfHeader(context.Request))
        {
            return Forbidden(
                $"Refresh-запрос должен содержать заголовок {CsrfHeaderName}: {CsrfHeaderValue}");
        }

        if (!IsOriginAllowed(context.Request, configuration))
        {
            return Forbidden("Источник запроса не разрешён");
        }

        var rawToken = context.Request.Cookies[RefreshCookieName];
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return Unauthorized("Refresh-токен отсутствует. Войдите заново");
        }

        var rotated = await refreshTokens.RotateAsync(rawToken, ct);
        if (rotated is null)
        {
            DeleteRefreshCookie(context);
            return Unauthorized("Refresh-токен недействителен. Войдите заново");
        }

        var profile = await profiles.FindAsync(rotated.Value.UserId, ct);
        if (profile is null)
        {
            DeleteRefreshCookie(context);
            return Unauthorized("Пользователь не найден. Войдите заново");
        }

        var accessToken = tokenService.IssueAccess(profile.Id, profile.DisplayName, profile.Permissions);
        SetRefreshCookie(context, rotated.Value.NewToken, rotated.Value.ExpiresAt);

        return Results.Ok(new TokenResponse(
            accessToken,
            (int)tokenService.AccessTokenLifetime.TotalSeconds));
    }

    /// <summary>Отзывает refresh-токен из cookie и очищает cookie.</summary>
    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        RefreshTokenService refreshTokens,
        CancellationToken ct)
    {
        var rawToken = context.Request.Cookies[RefreshCookieName];
        if (!string.IsNullOrWhiteSpace(rawToken))
        {
            await refreshTokens.RevokeAsync(rawToken, ct);
        }

        DeleteRefreshCookie(context);

        return Results.NoContent();
    }

    /// <summary>Профиль текущего пользователя: имя, роли из БД и права из role_claims.</summary>
    private static async Task<IResult> GetMeAsync(
        ICurrentUser currentUser,
        UserProfileService profiles,
        CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Unauthorized("Войдите, чтобы выполнить действие");
        }

        var profile = await profiles.FindAsync(userId, ct);
        if (profile is null)
        {
            return Unauthorized("Пользователь не найден. Войдите заново");
        }

        return Results.Ok(new MeResponse(
            profile.Id,
            profile.DisplayName,
            profile.Roles,
            profile.Permissions));
    }

    private static bool HasCsrfHeader(HttpRequest request)
    {
        var value = request.Headers[CsrfHeaderName];

        return value.Count == 1
            && string.Equals(value[0], CsrfHeaderValue, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Без <c>Origin</c> запрос считается same-origin (браузер его не присылает только для
    /// same-origin GET). Иначе Origin обязан совпасть с хостом запроса или с белым списком.
    /// </summary>
    private static bool IsOriginAllowed(HttpRequest request, IConfiguration configuration)
    {
        var originValues = request.Headers.Origin;
        if (originValues.Count == 0 || string.IsNullOrWhiteSpace(originValues.ToString()))
        {
            return true;
        }

        var origin = originValues.ToString().Trim();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
        {
            return false;
        }

        if (string.Equals(originUri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var allowedOrigins = configuration.GetSection(AllowedOriginsSection).Get<string[]>() ?? [];
        foreach (var allowed in allowedOrigins)
        {
            if (string.IsNullOrWhiteSpace(allowed))
            {
                continue;
            }

            if (Uri.TryCreate(allowed.Trim(), UriKind.Absolute, out var allowedUri)
                && string.Equals(
                    originUri.GetLeftPart(UriPartial.Authority),
                    allowedUri.GetLeftPart(UriPartial.Authority),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static void SetRefreshCookie(HttpContext context, string rawToken, DateTimeOffset expiresAt)
    {
        context.Response.Cookies.Append(
            RefreshCookieName,
            rawToken,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = RefreshCookiePath,
                Expires = expiresAt,
                IsEssential = true,
            });
    }

    private static void DeleteRefreshCookie(HttpContext context)
    {
        context.Response.Cookies.Delete(
            RefreshCookieName,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = RefreshCookiePath,
            });
    }

    private static IResult Unauthorized(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Не авторизован",
            detail: detail,
            type: "about:blank");

    private static IResult Forbidden(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Доступ запрещён",
            detail: detail,
            type: "about:blank");

    /// <summary>Ответ <c>POST /auth/refresh</c> по схеме <c>TokenResponse</c>.</summary>
    private sealed record TokenResponse(string AccessToken, int ExpiresIn);

    /// <summary>Ответ <c>GET /me</c> по схеме <c>Me</c>.</summary>
    private sealed record MeResponse(
        Guid Id,
        string DisplayName,
        IReadOnlyList<string> Roles,
        IReadOnlyList<string> Permissions);
}
