using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Api.IntegrationTests.Infrastructure;

using IdentityAccess.Contracts;

using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

namespace Api.IntegrationTests.Identity;

/// <summary>
/// Dev-only вход по логину и паролю: доступен только в Development/Testing, выдаёт те же
/// токены и refresh-cookie, что и обычные auth-эндпоинты, и выключен без
/// <c>DevAuth:Password</c>. Cookie передаётся заголовком вручную: у <see cref="HttpClient"/>
/// отключён cookie-контейнер.
/// </summary>
[Collection("postgres")]
public sealed class DevLoginTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly Guid AdminId = Guid.Parse("00000000-0000-0000-0000-0000000000a5");

    /// <summary>Dev-модератор Игорь (a4) из <c>db/seed_dev.sql</c>: MAX-id 900000004.</summary>
    private static readonly Guid IgorId = Guid.Parse("00000000-0000-0000-0000-0000000000a4");

    private TestApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new TestApiFactory(fixture.ConnectionString);
        _client = CreateClient(_factory);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Admin_dev_login_returns_token_and_refresh_cookie()
    {
        var ct = CancellationToken.None;

        using var response = await PostDevLoginAsync(
            _client,
            login: "admin",
            password: TestApiFactory.DevAuthPassword,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var setCookie = GetSetCookie(response);
        setCookie.ShouldContain("path=/api/v1/auth", Case.Insensitive);
        setCookie.ShouldContain("httponly", Case.Insensitive);
        setCookie.ShouldContain("secure", Case.Insensitive);
        setCookie.ShouldContain("samesite=strict", Case.Insensitive);

        string accessToken;
        using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)))
        {
            accessToken = json.RootElement.GetProperty("accessToken").GetString()!;
            accessToken.ShouldNotBeNullOrWhiteSpace();
            json.RootElement.GetProperty("expiresIn").GetInt32().ShouldBe(15 * 60);
        }

        using var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var meResponse = await _client.SendAsync(meRequest, ct);
        meResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var meJson = JsonDocument.Parse(await meResponse.Content.ReadAsStringAsync(ct));
        var me = meJson.RootElement;

        me.GetProperty("id").GetGuid().ShouldBe(AdminId);
        ReadStringArray(me.GetProperty("roles")).ShouldContain(Roles.Admin);
        ReadStringArray(me.GetProperty("permissions")).ShouldContain(Permissions.UsersRolesAssign);
    }

    [Fact]
    public async Task Dev_login_with_wrong_password_returns_401()
    {
        var ct = CancellationToken.None;

        using var response = await PostDevLoginAsync(
            _client,
            login: "admin",
            password: "wrong-password",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        json.RootElement.GetProperty("title").GetString().ShouldBe("Не авторизован");
        json.RootElement.GetProperty("detail").GetString().ShouldBe("Неверный логин или пароль");
    }

    [Fact]
    public async Task Dev_login_with_unknown_login_returns_401()
    {
        var ct = CancellationToken.None;

        using var response = await PostDevLoginAsync(
            _client,
            login: "nobody",
            password: TestApiFactory.DevAuthPassword,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        json.RootElement.GetProperty("title").GetString().ShouldBe("Не авторизован");
        json.RootElement.GetProperty("detail").GetString().ShouldBe("Неверный логин или пароль");
    }

    [Fact]
    public async Task Dev_login_by_max_id_returns_token_for_that_user()
    {
        var ct = CancellationToken.None;

        // Под dev-паролем можно войти любым пользователем из таблицы users: логин — user_name (MAX ID строкой).
        using var response = await PostDevLoginAsync(
            _client,
            login: "900000004",
            password: TestApiFactory.DevAuthPassword,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string accessToken;
        using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)))
        {
            accessToken = json.RootElement.GetProperty("accessToken").GetString()!;
            accessToken.ShouldNotBeNullOrWhiteSpace();
        }

        using var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var meResponse = await _client.SendAsync(meRequest, ct);
        meResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var meJson = JsonDocument.Parse(await meResponse.Content.ReadAsStringAsync(ct));
        var me = meJson.RootElement;

        me.GetProperty("id").GetGuid().ShouldBe(IgorId);
        me.GetProperty("displayName").GetString().ShouldBe("Игорь");
        ReadStringArray(me.GetProperty("roles")).ShouldContain(Roles.Moderator);
    }

    [Fact]
    public async Task Dev_login_by_unknown_max_id_returns_401()
    {
        var ct = CancellationToken.None;

        using var response = await PostDevLoginAsync(
            _client,
            login: "999999999",
            password: TestApiFactory.DevAuthPassword,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        json.RootElement.GetProperty("title").GetString().ShouldBe("Не авторизован");
        json.RootElement.GetProperty("detail").GetString().ShouldBe("Неверный логин или пароль");
    }

    [Fact]
    public async Task Dev_login_without_configured_password_returns_404()
    {
        var ct = CancellationToken.None;

        // Пустой DevAuth:Password выключает dev-вход: эндпоинт отвечает так, будто его нет.
        await using var factory = new TestApiFactory(
            fixture.ConnectionString,
            settings: new Dictionary<string, string?>
            {
                ["DevAuth:Password"] = string.Empty,
            });
        using var client = CreateClient(factory);

        using var response = await PostDevLoginAsync(
            client,
            login: "admin",
            password: TestApiFactory.DevAuthPassword,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    private static HttpClient CreateClient(TestApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });

    private static async Task<HttpResponseMessage> PostDevLoginAsync(
        HttpClient client,
        string login,
        string password,
        CancellationToken ct)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(new { login, password }),
            Encoding.UTF8,
            "application/json");

        return await client.PostAsync("/api/v1/auth/dev-login", content, ct);
    }

    private static string GetSetCookie(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("Set-Cookie", out var values).ShouldBeTrue(
            "Ответ должен содержать Set-Cookie");

        return values!.Single(value => value.StartsWith("refresh_token=", StringComparison.Ordinal));
    }

    private static string[] ReadStringArray(JsonElement element) =>
        element.EnumerateArray().Select(item => item.GetString()!).ToArray();
}
