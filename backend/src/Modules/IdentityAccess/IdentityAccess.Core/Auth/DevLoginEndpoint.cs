using System.Security.Cryptography;
using System.Text;

using IdentityAccess.Core.Seeding;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

namespace IdentityAccess.Core.Auth;

/// <summary>
/// Dev-only вход по логину и паролю. В продукте тестового входа нет: эндпоинт
/// регистрируется только в Development/Testing и выключен, пока не задан <c>DevAuth:Password</c>.
/// </summary>
public static class DevLoginEndpoint
{
    private const string PasswordSection = "DevAuth:Password";

    /// <summary>Логин dev-пользователя → его фиксированный GUID из <see cref="DevSeeder"/>.</summary>
    private static readonly Dictionary<string, Guid> DevLogins = new(StringComparer.Ordinal)
    {
        ["user"] = DevSeeder.UserId,
        ["moderator"] = DevSeeder.ModeratorId,
        ["admin"] = DevSeeder.AdminId,
    };

    /// <summary>Регистрирует <c>POST /auth/dev-login</c> внутри префикса <c>/api/v1</c>.</summary>
    public static IEndpointRouteBuilder MapDevLoginEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/auth/dev-login", DevLoginAsync);

        return endpoints;
    }

    /// <summary>
    /// Проверяет пароль из <c>DevAuth:Password</c> и логин: сначала алиасы из
    /// <see cref="DevLogins"/>, затем <c>user_name</c> любого пользователя из таблицы <c>users</c>.
    /// Затем выдаёт access-токен и refresh-cookie так же, как <c>POST /auth/refresh</c>.
    /// </summary>
    private static async Task<IResult> DevLoginAsync(
        DevLoginRequest request,
        HttpContext context,
        RefreshTokenService refreshTokens,
        TokenService tokenService,
        UserProfileService profiles,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var configuredPassword = configuration[PasswordSection];

        // Пустой или отсутствующий пароль — dev-вход выключен, эндпоинта как будто нет.
        if (string.IsNullOrEmpty(configuredPassword))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Не найдено",
                detail: "Dev-вход выключен",
                type: "about:blank");
        }

        var expected = Encoding.UTF8.GetBytes(configuredPassword);
        var actual = Encoding.UTF8.GetBytes(request.Password ?? string.Empty);

        // Пароль проверяется до любого обращения к БД.
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            return Unauthorized("Неверный логин или пароль");
        }

        if (string.IsNullOrWhiteSpace(request.Login))
        {
            return Unauthorized("Неверный логин или пароль");
        }

        // Сначала алиасы (user/moderator/admin), иначе — user_name (MAX ID строкой).
        var profile = DevLogins.TryGetValue(request.Login, out var aliasedUserId)
            ? await profiles.FindAsync(aliasedUserId, ct)
            : await profiles.FindByUserNameAsync(request.Login, ct);

        if (profile is null)
        {
            return Unauthorized("Неверный логин или пароль");
        }

        var accessToken = tokenService.IssueAccess(profile.Id, profile.DisplayName, profile.Permissions);
        var (refreshToken, expiresAt) = await refreshTokens.IssueAsync(profile.Id, ct);
        AuthEndpoints.SetRefreshCookie(context, refreshToken, expiresAt);

        return Results.Ok(new DevLoginResponse(
            accessToken,
            (int)tokenService.AccessTokenLifetime.TotalSeconds));
    }

    private static IResult Unauthorized(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Не авторизован",
            detail: detail,
            type: "about:blank");

    /// <summary>Тело <c>POST /auth/dev-login</c>; поля допускают <c>null</c> из JSON.</summary>
    private sealed record DevLoginRequest(string? Login, string? Password);

    /// <summary>Ответ по той же схеме <c>TokenResponse</c>, что у <c>POST /auth/refresh</c>.</summary>
    private sealed record DevLoginResponse(string AccessToken, int ExpiresIn);
}
