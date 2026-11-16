using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Api.IntegrationTests.Identity;
using Api.IntegrationTests.Infrastructure;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Shouldly;

namespace Api.IntegrationTests.Contract;

/// <summary>
/// Соответствие реализации <c>api/openapi.yaml</c> (этап 8): все операции
/// контракта есть в маршрутах хоста, лишних маршрутов нет, <c>x-permission</c> совпадает с
/// политикой авторизации эндпоинта, а фактические ответы входят в объявленные коды.
/// </summary>
/// <remarks>
/// Контракт читается из файла, а не из сборки: это источник правды, и тест должен падать,
/// если в контракт добавили метод без реализации (или наоборот). Хост поднимается на общей
/// PostGIS-БД коллекции с dev-seed (пользователи a1…a5, источник e1, фиксация f1).
/// </remarks>
[Collection("postgres")]
public sealed class OperationCoverageTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string OpenApiRelativePath = "api/openapi.yaml";

    /// <summary>Dev-пользователь роли <c>user</c> (a1).</summary>
    private static readonly Guid UserId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    /// <summary>Dev-модератор (a3).</summary>
    private static readonly Guid ModeratorId = Guid.Parse("00000000-0000-0000-0000-0000000000a3");

    /// <summary>Dev-администратор (a5).</summary>
    private static readonly Guid AdminId = Guid.Parse("00000000-0000-0000-0000-0000000000a5");

    /// <summary>Источник из dev-seed <c>db/seed_dev.sql</c>.</summary>
    private static readonly Guid SeedSourceId = Guid.Parse("00000000-0000-0000-0000-0000000000e1");

    /// <summary>Фиксация из dev-seed <c>db/seed_dev.sql</c>.</summary>
    private static readonly Guid SeedObjectId = Guid.Parse("00000000-0000-0000-0000-0000000000f1");

    /// <summary>
    /// Служебные маршруты хоста, которых нет в публичном контракте. Белый список явный:
    /// добавлять сюда бизнес-методы (например, <c>/sources/{id}/archive</c>) нельзя.
    /// </summary>
    private static readonly HashSet<(string Method, string Path)> ServiceEndpoints =
    [
        // Liveness-проба для Docker/оркестратора, вне /api/v1 и вне контракта.
        ("GET", "/health"),

        // dev-only, не входит в контракт: регистрируется только в Development/Testing
        // и выключен без DevAuth:Password. api/openapi.yaml не меняется.
        ("POST", "/api/v1/auth/dev-login"),
    ];

    private TestApiFactory _factory = null!;
    private HttpClient _client = null!;
    private OpenApiContract _contract = null!;
    private IReadOnlyList<Endpoint> _endpoints = null!;

    public async Task InitializeAsync()
    {
        var cancellationToken = CancellationToken.None;

        _contract = OpenApiContract.Load(FindOpenApiPath());

        _factory = new TestApiFactory(fixture.ConnectionString);
        _client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });

        // Первый запрос поднимает хост: миграции, роли, dev-пользователи и dev-каталог.
        using var response = await _client.GetAsync("/health", cancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        _endpoints = ReadEndpoints();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public void Every_operation_in_openapi_is_routed()
    {
        var routed = RoutedOperations();

        var missing = _contract.Operations
            .Where(operation => !routed.Contains((operation.Method, operation.Path)))
            .Select(operation => $"{operation.Method} {operation.Path} (x-permission: {operation.Permission})")
            .Order(StringComparer.Ordinal)
            .ToArray();

        missing.ShouldBeEmpty(
            "В Api.Host не реализованы операции контракта api/openapi.yaml:\n"
            + string.Join('\n', missing));
    }

    [Fact]
    public void Every_routed_endpoint_is_in_openapi()
    {
        var contractRoutes = _contract.Operations
            .Select(operation => (operation.Method, operation.Path))
            .ToHashSet();

        var extra = RoutedOperations()
            .Where(route => !contractRoutes.Contains(route))
            .Where(route => !ServiceEndpoints.Contains(route))
            .Select(route => $"{route.Method} {route.Path}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        extra.ShouldBeEmpty(
            "В Api.Host есть методы, которых нет в контракте (архивация источника, отладочные маршруты "
            + "и т. п. недопустимы):\n" + string.Join('\n', extra));
    }

    [Fact]
    public void Every_operation_declares_permission_matching_policy()
    {
        var problems = new List<string>();

        foreach (var endpoint in _endpoints.OfType<RouteEndpoint>())
        {
            var path = OpenApiContract.NormalizePath(endpoint.RoutePattern.RawText ?? string.Empty);
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];

            foreach (var method in methods)
            {
                if (!_contract.OperationsByRoute.TryGetValue(
                        (method.ToUpperInvariant(), path),
                        out var operation))
                {
                    // Лишний маршрут проверяет Every_routed_endpoint_is_in_openapi.
                    continue;
                }

                var authorization = DescribeAuthorization(endpoint);

                if (operation.IsPublic || operation.Permission == "none")
                {
                    if (!authorization.IsPublic)
                    {
                        problems.Add(
                            $"{operation.Method} {operation.Path}: в контракте "
                            + $"x-permission={operation.Permission}, security=[]/none, "
                            + $"а эндпоинт требует {Describe(authorization)}");
                    }

                    continue;
                }

                if (operation.Permission == "auth")
                {
                    if (authorization.IsPublic || authorization.Policies.Count > 0)
                    {
                        problems.Add(
                            $"{operation.Method} {operation.Path}: x-permission=auth требует только "
                            + $"аутентификацию, а эндпоинт: {Describe(authorization)}");
                    }

                    continue;
                }

                if (authorization.IsPublic)
                {
                    problems.Add(
                        $"{operation.Method} {operation.Path}: x-permission={operation.Permission}, "
                        + "а эндпоинт публичный");
                }
                else if (!authorization.Policies.SequenceEqual(
                             [operation.Permission],
                             StringComparer.Ordinal))
                {
                    problems.Add(
                        $"{operation.Method} {operation.Path}: x-permission={operation.Permission}, "
                        + $"а политика эндпоинта: {Describe(authorization)}");
                }
            }
        }

        problems.ShouldBeEmpty(
            "Политики авторизации не совпадают с x-permission контракта:\n"
            + string.Join('\n', problems));
    }

    [Fact]
    public async Task Responses_use_declared_status_codes()
    {
        var cancellationToken = CancellationToken.None;

        var userToken = await TestAuth.CreateAccessTokenAsync(_factory.Services, UserId, cancellationToken);
        var moderatorToken = await TestAuth.CreateAccessTokenAsync(_factory.Services, ModeratorId, cancellationToken);
        var adminToken = await TestAuth.CreateAccessTokenAsync(_factory.Services, AdminId, cancellationToken);

        var unknown = Guid.NewGuid();

        // Выборка операций с seed-данными: по каждой ветке контракта — успешный ответ,
        // отказ в доступе, отсутствующий ресурс или валидация. Проверяется вхождение
        // фактического кода в объявленные, а не точное совпадение: контракт описывает
        // несколько допустимых исходов (404/409/422 и т. п.).
        Probe[] probes =
        [
            new("POST", "/api/v1/auth/max", null, null, "заглушка входа MAX"),
            new("POST", "/api/v1/auth/refresh", null, null, "refresh без cookie и CSRF"),
            new("GET", "/api/v1/me", null, null, "профиль без токена"),
            new("GET", "/api/v1/me", adminToken, null, "профиль администратора"),
            new("GET", "/api/v1/map/features?bbox=37.7,47.9,37.9,48.1", null, null, "карта анонимно"),
            new("GET", $"/api/v1/map/objects/{SeedObjectId}", null, null, "карточка фиксации"),
            new("GET", $"/api/v1/map/objects/{unknown}", null, null, "несуществующая фиксация"),
            new("GET", $"/api/v1/objects/{SeedObjectId}/observations", null, null, "публичный список отметок"),
            new("POST", "/api/v1/observations", null, "{}", "отметка без токена"),
            new("POST", "/api/v1/observations", userToken, "{}", "отметка с пустым телом"),
            new("GET", "/api/v1/sources", null, null, "источники без токена"),
            new("GET", "/api/v1/sources", userToken, null, "источники пользователем"),
            new("GET", "/api/v1/sources", adminToken, null, "список источников"),
            new("GET", $"/api/v1/sources/{SeedSourceId}", adminToken, null, "источник из seed"),
            new("GET", $"/api/v1/sources/{unknown}", adminToken, null, "несуществующий источник"),
            new("POST", $"/api/v1/sources/{SeedSourceId}/enable", adminToken, null, "включение источника"),
            new("GET", $"/api/v1/sources/{SeedSourceId}/health", adminToken, null, "health источника"),
            new("GET", $"/api/v1/sources/{SeedSourceId}/statuses", adminToken, null, "статусы источника"),
            new("DELETE", $"/api/v1/sources/{unknown}", adminToken, null, "удаление несуществующего источника"),
            new("POST", "/api/v1/auth/logout", null, null, "выход без токена"),
            new("POST", "/api/v1/auth/logout", adminToken, null, "выход"),
            new("GET", "/api/v1/admin/users", moderatorToken, null, "пользователи модератором"),
            new("GET", "/api/v1/admin/users", adminToken, null, "пользователи администратором"),
            new("GET", "/api/v1/admin/audit", adminToken, null, "журнал аудита"),
            new("GET", "/api/v1/admin/settings", adminToken, null, "глобальные настройки"),
            new("GET", "/api/v1/admin/secrets", adminToken, null, "список секретов"),
            new("GET", $"/api/v1/admin/sources/{SeedSourceId}/connection", adminToken, null, "подключение источника"),
            new("GET", "/api/v1/catalog/categories", null, null, "категории анонимно"),
            new("GET", "/api/v1/catalog/object-types", null, null, "типы объектов анонимно"),
            new("GET", "/api/v1/catalog/groups", null, null, "группы анонимно"),
            new("GET", "/api/v1/catalog/statuses", null, null, "справочник статусов анонимно"),
            new("GET", "/api/v1/catalog/objects", adminToken, null, "фиксации в редакторе"),
            new("GET", $"/api/v1/catalog/objects/{SeedObjectId}", adminToken, null, "фиксация в редакторе"),
            new("GET", $"/api/v1/catalog/objects/{unknown}", adminToken, null, "несуществующая фиксация в редакторе"),
            new("PUT", $"/api/v1/catalog/categories/{unknown}", adminToken, "{}", "правка несуществующей категории"),
            new("DELETE", $"/api/v1/catalog/categories/{unknown}", adminToken, null, "удаление несуществующей категории"),
            new("GET", "/api/v1/moderation/cases", moderatorToken, null, "очередь модерации"),
            new("GET", $"/api/v1/moderation/cases/{unknown}", moderatorToken, null, "несуществующее дело"),
            new("POST", $"/api/v1/moderation/cases/{unknown}/approve", moderatorToken, "{}", "одобрение несуществующего дела"),
            new("POST", $"/api/v1/observations/{unknown}/retract", moderatorToken, "{}", "отзыв несуществующей отметки"),
            new("POST", "/api/v1/observations/retract-bulk", moderatorToken, "{}", "пакетный отзыв без фильтра"),
        ];

        var failures = new List<string>();

        foreach (var probe in probes)
        {
            using var request = BuildRequest(probe);
            using var response = await _client.SendAsync(request, cancellationToken);

            var status = (int)response.StatusCode;
            var route = probe.Path.Split('?', 2)[0];
            var operation = _contract.FindOperation(probe.Method, route)
                ?? throw new InvalidOperationException(
                    $"Операции {probe.Method} {route} нет в контракте.");

            if (!operation.StatusCodes.Contains(status))
            {
                failures.Add(
                    $"{probe.Description}: {probe.Method} {probe.Path} → {status}, "
                    + $"в контракте {string.Join('/', operation.StatusCodes)}");
            }
        }

        failures.ShouldBeEmpty(
            "Ответы Api не входят в объявленные контрактом коды:\n" + string.Join('\n', failures));
    }

    private static HttpRequestMessage BuildRequest(Probe probe)
    {
        var request = new HttpRequestMessage(new HttpMethod(probe.Method), probe.Path);

        if (probe.AccessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", probe.AccessToken);
        }

        if (probe.Json is not null)
        {
            request.Content = new StringContent(probe.Json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private HashSet<(string Method, string Path)> RoutedOperations()
    {
        var routes = new HashSet<(string Method, string Path)>();

        foreach (var endpoint in _endpoints.OfType<RouteEndpoint>())
        {
            if (endpoint.RoutePattern.RawText is not { } rawPath)
            {
                continue;
            }

            var path = OpenApiContract.NormalizePath(rawPath);
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;

            if (methods is null || methods.Count == 0)
            {
                continue;
            }

            foreach (var method in methods)
            {
                routes.Add((method.ToUpperInvariant(), path));
            }
        }

        return routes;
    }

    private IReadOnlyList<Endpoint> ReadEndpoints() =>
        _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

    private static AuthorizationDescription DescribeAuthorization(Endpoint endpoint)
    {
        var allowAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
        var data = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();

        var policies = data
            .Select(item => item.Policy)
            .Where(policy => !string.IsNullOrWhiteSpace(policy))
            .Select(policy => policy!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new AuthorizationDescription(allowAnonymous || data.Count == 0, policies);
    }

    private static string Describe(AuthorizationDescription description) =>
        description.IsPublic
            ? "публичный"
            : description.Policies.Count == 0
                ? "только аутентификация"
                : $"политика {string.Join(", ", description.Policies)}";

    /// <summary>
    /// Ищет корень репозитория подъёмом вверх от каталога тестовой сборки: в нём лежит
    /// <c>api/openapi.yaml</c> (как <c>SchemaParityTests</c> ищет <c>db/schema.sql</c>).
    /// </summary>
    private static string FindOpenApiPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, OpenApiRelativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Не найден {OpenApiRelativePath} ни в одном из родительских каталогов '{AppContext.BaseDirectory}'.");
    }

    private sealed record Probe(
        string Method,
        string Path,
        string? AccessToken,
        string? Json,
        string Description);

    private sealed record AuthorizationDescription(bool IsPublic, IReadOnlyList<string> Policies);
}
