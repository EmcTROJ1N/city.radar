using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Api.IntegrationTests.Catalog;
using Api.IntegrationTests.Identity;
using Api.IntegrationTests.Infrastructure;

using Ingestion.Abstractions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Shouldly;

using Sources.Contracts;

namespace Api.IntegrationTests.Sources;

/// <summary>
/// Health источника: новый источник — <c>unknown</c>,
/// состояния и детали от <see cref="ISourceHealthReporter"/>, вычисляемый <c>silent</c> по
/// времени последнего сообщения, отсутствие <c>silent</c> у выключенного источника, 404 и права.
/// Хост Api поднимается на общей БД коллекции; изоляция — уникальные названия на каждый тест.
/// </summary>
[Collection("postgres")]
public sealed class HealthTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string SourcesPath = "/api/v1/sources";

    private CatalogApi _api = null!;

    public async Task InitializeAsync()
    {
        _api = new CatalogApi(fixture);
        await _api.InitializeAsync();
    }

    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task New_source_is_unknown()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var sourceId = await _api.CreateSourceAsync(token, ct: ct);

        using var response = await _api.GetAsync(HealthPath(sourceId), token, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var health = await CatalogApi.ReadJsonAsync(response, ct);
        health.GetProperty("state").GetString().ShouldBe("unknown");
        health.GetProperty("lastMessageAt").ValueKind.ShouldBe(JsonValueKind.Null);
        health.GetProperty("floodWaitUntil").ValueKind.ShouldBe(JsonValueKind.Null);
        health.GetProperty("lastError").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Reported_flood_wait_and_auth_error_are_returned()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var sourceId = await _api.CreateSourceAsync(token, ct: ct);
        var until = DateTimeOffset.UtcNow.AddMinutes(45);

        // Health — служебные данные: отчёты воркера не создают записей аудита (в отличие от
        // source.create самого источника).
        var auditBefore = await _api.CountAsync(
            $"select count(*) from audit_entries where entity_id = '{sourceId}'",
            ct);

        await ReportAsync(
            _api.Factory.Services,
            sourceId,
            SourceState.FloodWait,
            until.ToString("O", CultureInfo.InvariantCulture),
            lastMessageAt: null,
            ct);

        using (var response = await _api.GetAsync(HealthPath(sourceId), token, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var health = await CatalogApi.ReadJsonAsync(response, ct);
            health.GetProperty("state").GetString().ShouldBe("flood_wait");
            health.GetProperty("floodWaitUntil").GetDateTimeOffset().ToUnixTimeSeconds()
                .ShouldBe(until.ToUnixTimeSeconds());
            health.GetProperty("lastError").ValueKind.ShouldBe(JsonValueKind.Null);
        }

        const string authDetail = "Сессия недействительна: аккаунт разлогинен";
        await ReportAsync(
            _api.Factory.Services,
            sourceId,
            SourceState.AuthError,
            authDetail,
            lastMessageAt: null,
            ct);

        using (var response = await _api.GetAsync(HealthPath(sourceId), token, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var health = await CatalogApi.ReadJsonAsync(response, ct);
            health.GetProperty("state").GetString().ShouldBe("auth_error");
            health.GetProperty("floodWaitUntil").ValueKind.ShouldBe(JsonValueKind.Null);
            health.GetProperty("lastError").GetString().ShouldBe(authDetail);
        }

        // Health — служебные данные: отчёты воркера не создают записей аудита.
        (await _api.CountAsync(
            $"select count(*) from audit_entries where entity_id = '{sourceId}'",
            ct)).ShouldBe(auditBefore);
    }

    [Fact]
    public async Task Silent_is_computed_when_last_message_is_older_than_threshold()
    {
        var ct = CancellationToken.None;
        var now = DateTimeOffset.UtcNow;

        using var factory = new TestApiFactory(fixture.ConnectionString);
        using var overridden = WithTime(factory, now);
        using var client = CreateClient(overridden);
        var token = await TestAuth.CreateAccessTokenAsync(
            overridden.Services,
            CatalogApi.ModeratorId,
            ct);
        var sourceId = await CreateSourceAsync(client, token, ct);

        await ReportAsync(
            overridden.Services,
            sourceId,
            SourceState.Ok,
            detail: null,
            lastMessageAt: now.AddHours(-7),
            ct);

        using var response = await GetHealthAsync(client, sourceId, token, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var health = await CatalogApi.ReadJsonAsync(response, ct);
        health.GetProperty("state").GetString().ShouldBe("silent");
        health.GetProperty("lastMessageAt").GetDateTimeOffset()
            .ToUnixTimeSeconds()
            .ShouldBe(now.AddHours(-7).ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Ok_with_recent_message_stays_ok()
    {
        var ct = CancellationToken.None;
        var now = DateTimeOffset.UtcNow;

        using var factory = new TestApiFactory(fixture.ConnectionString);
        using var overridden = WithTime(factory, now);
        using var client = CreateClient(overridden);
        var token = await TestAuth.CreateAccessTokenAsync(
            overridden.Services,
            CatalogApi.ModeratorId,
            ct);
        var sourceId = await CreateSourceAsync(client, token, ct);

        await ReportAsync(
            overridden.Services,
            sourceId,
            SourceState.Ok,
            detail: null,
            lastMessageAt: now.AddHours(-1),
            ct);

        using var response = await GetHealthAsync(client, sourceId, token, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var health = await CatalogApi.ReadJsonAsync(response, ct);
        health.GetProperty("state").GetString().ShouldBe("ok");
    }

    [Fact]
    public async Task Disabled_source_is_not_silent()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var sourceId = await _api.CreateSourceAsync(token, ct: ct);

        await ReportAsync(
            _api.Factory.Services,
            sourceId,
            SourceState.Ok,
            detail: null,
            lastMessageAt: DateTimeOffset.UtcNow.AddHours(-7),
            ct);

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{sourceId}/disable",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var response = await _api.GetAsync(HealthPath(sourceId), token, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var health = await CatalogApi.ReadJsonAsync(response, ct);
            health.GetProperty("state").GetString().ShouldBe("ok");
        }
    }

    [Fact]
    public async Task Missing_source_404()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);

        using var response = await _api.GetAsync(HealthPath(Guid.NewGuid()), token, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Moderator_200_user_403_anonymous_401()
    {
        var ct = CancellationToken.None;
        var moderatorToken = await _api.ModeratorTokenAsync(ct);
        var userToken = await _api.UserTokenAsync(ct);
        var sourceId = await _api.CreateSourceAsync(moderatorToken, ct: ct);

        using (var response = await _api.GetAsync(HealthPath(sourceId), accessToken: null, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var response = await _api.GetAsync(HealthPath(sourceId), userToken, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var response = await _api.GetAsync(HealthPath(sourceId), moderatorToken, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    private static string HealthPath(Guid sourceId) => $"{SourcesPath}/{sourceId}/health";

    private static async Task ReportAsync(
        IServiceProvider services,
        Guid sourceId,
        SourceState state,
        string? detail,
        DateTimeOffset? lastMessageAt,
        CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var reporter = scope.ServiceProvider.GetRequiredService<ISourceHealthReporter>();

        await reporter.ReportAsync(sourceId, state, detail, lastMessageAt, ct);
    }

    private static WebApplicationFactory<Program> WithTime(
        TestApiFactory factory,
        DateTimeOffset now) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
        }));

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });

    private static async Task<Guid> CreateSourceAsync(
        HttpClient client,
        string token,
        CancellationToken ct)
    {
        var suffix = CatalogApi.NewSuffix();
        var request = new HttpRequestMessage(HttpMethod.Post, SourcesPath)
        {
            Content = new StringContent(
                $$"""
                  {"name":"Источник health {{suffix}}","sourceType":"telegram_chat",
                   "chatId":"@health_{{suffix}}","timeoutSeconds":3600,"userPrompt":""}
                  """,
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> GetHealthAsync(
        HttpClient client,
        Guid sourceId,
        string token,
        CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, HealthPath(sourceId));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return client.SendAsync(request, ct);
    }

    /// <summary>Время, зафиксированное на момент теста: проверка порога «тишины» без ожидания.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
