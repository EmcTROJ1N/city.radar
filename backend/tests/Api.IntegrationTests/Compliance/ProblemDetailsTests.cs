using System.Net;
using System.Text.Json;

using Api.IntegrationTests.Catalog;
using Api.IntegrationTests.Infrastructure;

using BuildingBlocks.Messaging;

using Microsoft.AspNetCore.Http;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Shouldly;

namespace Api.IntegrationTests.Compliance;

/// <summary>
/// Все ответы с кодом ≥ 400 — Problem Details RFC 7807: <c>application/problem+json</c>,
/// поля <c>title</c>/<c>status</c>, без стека вызовов, SQL и строки подключения. 422 — по схеме
/// <c>ValidationProblem</c> с массивом <c>errors[{field,message}]</c>. Проверяются 400 (парсеры),
/// 401, 403, 404, 409, 422, 429 и необработанное исключение (500).
/// </summary>
[Collection("postgres")]
public sealed class ProblemDetailsTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string CategoriesPath = "/api/v1/catalog/categories";
    private const string SecretsPath = "/api/v1/admin/secrets";

    private ComplianceApi _api = null!;
    private ComplianceData _data = null!;
    private string _moderatorToken = null!;

    public async Task InitializeAsync()
    {
        var ct = CancellationToken.None;

        _api = new ComplianceApi(fixture);
        await _api.InitializeAsync();

        _data = await _api.SeedAsync(ct);
        _moderatorToken = await _api.ModeratorTokenAsync(ct);
    }

    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task Unauthorized_401_is_problem_details()
    {
        var ct = CancellationToken.None;

        using var response = await _api.GetAsync(SecretsPath, accessToken: null, ct);

        await ProblemAssertions.AssertProblemAsync(response, StatusCodes.Status401Unauthorized, ct);
    }

    [Fact]
    public async Task Forbidden_403_is_problem_details()
    {
        var ct = CancellationToken.None;

        using var response = await _api.GetAsync(SecretsPath, _moderatorToken, ct);

        await ProblemAssertions.AssertProblemAsync(response, StatusCodes.Status403Forbidden, ct);
    }

    [Fact]
    public async Task NotFound_404_is_problem_details()
    {
        var ct = CancellationToken.None;

        using var response = await _api.GetAsync(
            $"/api/v1/catalog/objects/{Guid.NewGuid()}",
            _moderatorToken,
            ct);

        await ProblemAssertions.AssertProblemAsync(response, StatusCodes.Status404NotFound, ct);
    }

    [Fact]
    public async Task Conflict_409_is_problem_details()
    {
        var ct = CancellationToken.None;
        var code = $"dup_{CatalogApi.NewSuffix()}";

        using (var created = await _api.SendAsync(
            HttpMethod.Post,
            CategoriesPath,
            _moderatorToken,
            $$"""{"code":"{{code}}","name":"Категория"}""",
            ct))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            CategoriesPath,
            _moderatorToken,
            $$"""{"code":"{{code}}","name":"Категория"}""",
            ct);

        await ProblemAssertions.AssertProblemAsync(response, StatusCodes.Status409Conflict, ct);
    }

    [Fact]
    public async Task Validation_422_is_validation_problem()
    {
        var ct = CancellationToken.None;
        var code = $"invalid_{CatalogApi.NewSuffix()}";

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            CategoriesPath,
            _moderatorToken,
            $$"""{"code":"{{code}}"}""",
            ct);

        var root = await ProblemAssertions.AssertValidationAsync(response, "name", ct);

        root.GetProperty("errors").EnumerateArray().ShouldAllBe(error =>
            error.GetProperty("field").ValueKind == JsonValueKind.String
            && error.GetProperty("message").ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task Bad_request_from_query_parsers_is_problem_details()
    {
        var ct = CancellationToken.None;

        // BboxParser: не четыре числа (MapReadModel).
        using (var response = await _api.GetAsync("/api/v1/map/features?bbox=oops", accessToken: null, ct))
        {
            await ProblemAssertions.AssertProblemAsync(
                response,
                StatusCodes.Status400BadRequest,
                ct);
        }

        // PageRequest: limit вне диапазона (Sources).
        using (var response = await _api.GetAsync(
            "/api/v1/sources?limit=0",
            _moderatorToken,
            ct))
        {
            await ProblemAssertions.AssertProblemAsync(
                response,
                StatusCodes.Status400BadRequest,
                ct);
        }
    }

    [Fact]
    public async Task Rate_limit_429_is_problem_details()
    {
        var ct = CancellationToken.None;

        // Отдельный хост с включённым лимитом: глобальная настройка ограничивает одну
        // отметку в час, второй запрос того же пользователя получает 429.
        await using var limited = new ComplianceApi(
            fixture,
            settings: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["RateLimiting:Enabled"] = "true",
            });
        await limited.InitializeAsync();

        await limited.ExecuteAsync(
            "insert into global_settings (key, value) values ('observation_rate_per_hour', '1') "
            + "on conflict (key) do update set value = excluded.value",
            ct);

        try
        {
            var userToken = await limited.UserTokenAsync(ct);
            var body =
                $$"""
                  {"mapObjectId":"{{_data.ObjectId}}","sourceId":"{{_data.SourceId}}",
                   "sourceStatusId":"{{_data.StatusId}}"}
                  """;

            using (var first = await limited.SendAsync(
                HttpMethod.Post,
                "/api/v1/observations",
                userToken,
                body,
                ct))
            {
                first.StatusCode.ShouldBe(HttpStatusCode.Created);
            }

            using var second = await limited.SendAsync(
                HttpMethod.Post,
                "/api/v1/observations",
                userToken,
                body,
                ct);

            await ProblemAssertions.AssertProblemAsync(
                second,
                StatusCodes.Status429TooManyRequests,
                ct);
        }
        finally
        {
            await limited.ExecuteAsync(
                "delete from global_settings where key = 'observation_rate_per_hour'",
                ct);
        }
    }

    [Fact]
    public async Task Unhandled_exception_500_is_problem_details_without_internals()
    {
        var ct = CancellationToken.None;

        await using var failing = new ComplianceApi(
            fixture,
            services =>
            {
                services.RemoveAll<IOutbox>();
                services.AddScoped<IOutbox, ThrowingOutbox>();
            });
        await failing.InitializeAsync();

        var userToken = await failing.UserTokenAsync(ct);

        using var response = await failing.SendAsync(
            HttpMethod.Post,
            "/api/v1/observations",
            userToken,
            $$"""
              {"mapObjectId":"{{_data.ObjectId}}","sourceId":"{{_data.SourceId}}",
               "sourceStatusId":"{{_data.StatusId}}"}
              """,
            ct);

        var root = await ProblemAssertions.AssertProblemAsync(
            response,
            StatusCodes.Status500InternalServerError,
            ct);

        root.GetProperty("title").GetString().ShouldBe("Внутренняя ошибка сервера");
    }

    private sealed class ThrowingOutbox : IOutbox
    {
        public void Enqueue(string destination, string type, object payload) =>
            throw new InvalidOperationException("Тестовый сбой outbox для проверки 500");
    }
}
