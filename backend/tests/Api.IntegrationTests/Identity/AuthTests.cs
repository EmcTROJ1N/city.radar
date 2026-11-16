using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Api.IntegrationTests.Infrastructure;

using IdentityAccess.Contracts;
using IdentityAccess.Core.Auth;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Shouldly;

namespace Api.IntegrationTests.Identity;

/// <summary>
/// JWT, refresh-ротация и защита сессий (Review Focus п. 5). Каждый тест поднимает
/// собственный хост на общей БД коллекции: сидеры идемпотентны, а выданные токены уникальны.
/// Cookie передаётся заголовком вручную: у <see cref="HttpClient"/> отключён cookie-контейнер.
/// </summary>
[Collection("postgres")]
public sealed class AuthTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly Guid ModeratorId = Guid.Parse("00000000-0000-0000-0000-0000000000a3");
    private static readonly Guid UserId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private TestApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new TestApiFactory(fixture.ConnectionString);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Auth_max_returns_501()
    {
        var ct = CancellationToken.None;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/max");
        using var response = await _client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        json.RootElement.GetProperty("title").GetString().ShouldBe("Не реализовано");
        json.RootElement.GetProperty("status").GetInt32().ShouldBe(501);
        json.RootElement.GetProperty("detail").GetString().ShouldBe("Авторизация будет доступна позже");
    }

    [Fact]
    public async Task Me_returns_permissions_of_roles()
    {
        var ct = CancellationToken.None;
        var accessToken = await TestAuth.CreateAccessTokenAsync(_factory.Services, ModeratorId, ct);

        using var request = CreateAuthorizedRequest(HttpMethod.Get, "/api/v1/me", accessToken);
        using var response = await _client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var me = json.RootElement;

        me.GetProperty("id").GetGuid().ShouldBe(ModeratorId);
        me.GetProperty("displayName").GetString().ShouldBe("Ольга");

        ReadStringArray(me.GetProperty("roles")).ShouldBe([Roles.Moderator]);

        var expectedPermissions = Roles.PermissionsFor(Roles.Moderator)
            .Order(StringComparer.Ordinal)
            .ToArray();
        ReadStringArray(me.GetProperty("permissions")).ShouldBe(expectedPermissions);
    }

    [Fact]
    public async Task Anonymous_me_returns_401()
    {
        var ct = CancellationToken.None;

        using var response = await _client.GetAsync(new Uri("/api/v1/me", UriKind.Relative), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Refresh_rotates_token_and_old_one_is_rejected()
    {
        var ct = CancellationToken.None;
        var (oldToken, _) = await IssueRefreshAsync(ModeratorId, ct);

        string newToken;
        using (var response = await SendRefreshAsync(oldToken, ct: ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var setCookie = GetSetCookie(response);
            setCookie.ShouldContain("path=/api/v1/auth", Case.Insensitive);
            setCookie.ShouldContain("httponly", Case.Insensitive);
            setCookie.ShouldContain("secure", Case.Insensitive);
            setCookie.ShouldContain("samesite=strict", Case.Insensitive);

            newToken = ReadRefreshCookie(response);
            newToken.ShouldNotBe(oldToken);

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            json.RootElement.GetProperty("accessToken").GetString().ShouldNotBeNullOrWhiteSpace();
            json.RootElement.GetProperty("expiresIn").GetInt32().ShouldBe(15 * 60);
        }

        // Старый токен после ротации отозван и больше не принимается.
        using var rejected = await SendRefreshAsync(oldToken, ct: ct);
        rejected.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        rejected.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Reused_revoked_refresh_token_returns_401_and_revokes_family()
    {
        var ct = CancellationToken.None;
        var (oldToken, _) = await IssueRefreshAsync(ModeratorId, ct);
        var newToken = await RotateAsync(oldToken, ct);

        using (var reuse = await SendRefreshAsync(oldToken, ct: ct))
        {
            reuse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // Повторное использование старого токена — признак кражи: вся семья отозвана.
        using var family = await SendRefreshAsync(newToken, ct: ct);
        family.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Expired_refresh_returns_401()
    {
        var ct = CancellationToken.None;
        var rawToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));

        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync(ct);

            await using var command = new NpgsqlCommand(
                """
                insert into refresh_tokens (user_id, token_hash, created_at, expires_at)
                values (@user_id, @token_hash, now() - interval '40 days', now() - interval '1 day')
                """,
                connection);
            command.Parameters.AddWithValue("user_id", ModeratorId);
            command.Parameters.AddWithValue("token_hash", tokenHash);

            await command.ExecuteNonQueryAsync(ct);
        }

        using var response = await SendRefreshAsync(rawToken, ct: ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Refresh_without_cookie_returns_401()
    {
        var ct = CancellationToken.None;

        using var response = await SendRefreshAsync(rawToken: null, ct: ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Refresh_without_csrf_header_returns_403()
    {
        var ct = CancellationToken.None;
        var (rawToken, _) = await IssueRefreshAsync(ModeratorId, ct);

        using var response = await SendRefreshAsync(rawToken, withCsrf: false, ct: ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        // Даже с валидным токеном access-токен не выдан: cookie не ротирована.
        using var retry = await SendRefreshAsync(rawToken, ct: ct);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_with_foreign_origin_returns_403()
    {
        var ct = CancellationToken.None;
        var (rawToken, _) = await IssueRefreshAsync(ModeratorId, ct);

        using var response = await SendRefreshAsync(rawToken, origin: "https://evil.example", ct: ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Refresh_with_same_host_origin_after_proxy_dropped_port_succeeds()
    {
        var ct = CancellationToken.None;
        var (rawToken, _) = await IssueRefreshAsync(ModeratorId, ct);

        // `proxy_set_header Host $host` отбрасывает порт: Origin приходит с портом, Host — без.
        using var response = await SendRefreshAsync(rawToken, origin: "https://localhost:4201", ct: ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_with_origin_on_another_port_returns_403()
    {
        var ct = CancellationToken.None;
        var (rawToken, _) = await IssueRefreshAsync(ModeratorId, ct);

        // Порт в Host передан явно и не совпадает с портом Origin — это чужой источник.
        using var response = await SendRefreshAsync(
            rawToken,
            origin: "https://localhost:4201",
            ct: ct,
            host: "localhost:8443");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Logout_revokes_refresh()
    {
        var ct = CancellationToken.None;
        var (rawToken, _) = await IssueRefreshAsync(ModeratorId, ct);
        var accessToken = await TestAuth.CreateAccessTokenAsync(_factory.Services, ModeratorId, ct);

        using (var request = CreateAuthorizedRequest(HttpMethod.Post, "/api/v1/auth/logout", accessToken))
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"refresh_token={rawToken}");

            using var response = await _client.SendAsync(request, ct);

            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            GetSetCookie(response).ShouldContain("refresh_token=", Case.Insensitive);
        }

        using var refresh = await SendRefreshAsync(rawToken, ct: ct);
        refresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Concurrent_refresh_with_same_token_succeeds_once()
    {
        var ct = CancellationToken.None;
        var (rawToken, _) = await IssueRefreshAsync(ModeratorId, ct);

        var requests = Enumerable.Range(0, 10)
            .Select(_ => RefreshStatusAsync(rawToken, ct))
            .ToArray();

        var statuses = await Task.WhenAll(requests);

        statuses.Count(status => status == HttpStatusCode.OK).ShouldBe(1);
        statuses.Count(status => status == HttpStatusCode.Unauthorized).ShouldBe(9);
    }

    [Fact]
    public async Task Policy_denies_user_without_permission()
    {
        var ct = CancellationToken.None;
        var authorization = _factory.Services.GetRequiredService<IAuthorizationService>();

        // Реальный JWT проходит через JwtBearer-хендлер: права из claim'ов токена.
        var userToken = await TestAuth.CreateAccessTokenAsync(_factory.Services, UserId, ct);
        var user = await AuthenticateAsync(userToken, ct);

        (await authorization.AuthorizeAsync(user, Permissions.MapRead)).Succeeded.ShouldBeTrue();

        // Право audit.read есть только у admin: у user и moderator политика должна отказать.
        (await authorization.AuthorizeAsync(user, Permissions.AuditRead)).Succeeded.ShouldBeFalse();

        var moderator = CreatePrincipal(ModeratorId, Roles.PermissionsFor(Roles.Moderator));
        (await authorization.AuthorizeAsync(moderator, Permissions.AuditRead)).Succeeded.ShouldBeFalse();
        (await authorization.AuthorizeAsync(moderator, Permissions.SourceManage)).Succeeded.ShouldBeTrue();
    }

    private async Task<(string RawToken, DateTimeOffset ExpiresAt)> IssueRefreshAsync(
        Guid userId,
        CancellationToken ct)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var refreshTokens = scope.ServiceProvider.GetRequiredService<RefreshTokenService>();

        return await refreshTokens.IssueAsync(userId, ct);
    }

    private async Task<HttpResponseMessage> SendRefreshAsync(
        string? rawToken,
        bool withCsrf = true,
        string? origin = null,
        CancellationToken ct = default,
        string? host = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");

        if (host is not null)
        {
            request.Headers.Host = host;
        }

        if (withCsrf)
        {
            request.Headers.TryAddWithoutValidation("X-Requested-With", "mayak");
        }

        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        if (rawToken is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"refresh_token={rawToken}");
        }

        return await _client.SendAsync(request, ct);
    }

    private async Task<HttpStatusCode> RefreshStatusAsync(string rawToken, CancellationToken ct)
    {
        using var response = await SendRefreshAsync(rawToken, ct: ct);

        return response.StatusCode;
    }

    private async Task<string> RotateAsync(string rawToken, CancellationToken ct)
    {
        using var response = await SendRefreshAsync(rawToken, ct: ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return ReadRefreshCookie(response);
    }

    /// <summary>Прогоняет access-токен через настоящий JwtBearer-хендлер из тестового хоста.</summary>
    private async Task<ClaimsPrincipal> AuthenticateAsync(string accessToken, CancellationToken ct)
    {
        await using var scope = _factory.Services.CreateAsyncScope();

        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers.Authorization = $"Bearer {accessToken}";

        var authentication = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
        var result = await authentication.AuthenticateAsync(
            context,
            JwtBearerDefaults.AuthenticationScheme);

        result.Succeeded.ShouldBeTrue(result.Failure?.Message);

        return result.Principal!;
    }

    private static HttpRequestMessage CreateAuthorizedRequest(
        HttpMethod method,
        string path,
        string accessToken)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return request;
    }

    private static string GetSetCookie(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("Set-Cookie", out var values).ShouldBeTrue(
            "Ответ должен содержать Set-Cookie");

        return values!.Single(value => value.StartsWith("refresh_token=", StringComparison.Ordinal));
    }

    private static string ReadRefreshCookie(HttpResponseMessage response)
    {
        var cookie = GetSetCookie(response);
        var pair = cookie.Split(';', 2)[0];

        return pair["refresh_token=".Length..];
    }

    private static string[] ReadStringArray(JsonElement element) =>
        element.EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static ClaimsPrincipal CreatePrincipal(Guid userId, IEnumerable<string> permissions)
    {
        var claims = new List<Claim>
        {
            new("sub", userId.ToString()),
            new("name", "Тестовый пользователь"),
        };
        claims.AddRange(permissions.Select(permission => new Claim("permission", permission)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }
}
