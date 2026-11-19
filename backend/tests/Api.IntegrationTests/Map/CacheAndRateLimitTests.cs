using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;

using Api.IntegrationTests.Catalog;
using Api.IntegrationTests.Identity;
using Api.IntegrationTests.Infrastructure;

using BuildingBlocks.Web.RateLimiting;

using MapReadModel.Core;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Shouldly;

namespace Api.IntegrationTests.Map;

/// <summary>
/// Кэш ответа карты и rate limit отметок.
/// Хост Api поднимается на общей БД коллекции и на Redis из <see cref="RedisFixture"/>
/// (<c>redis:7-alpine</c>); перед каждым тестом Redis очищается, поэтому счётчики и кэш
/// предыдущих тестов не влияют на следующий.
/// </summary>
[Collection("postgres")]
public sealed class CacheAndRateLimitTests(PostgresFixture fixture, RedisFixture redis)
    : IAsyncLifetime, IClassFixture<RedisFixture>
{
    private const string FeaturesPath = "/api/v1/map/features";
    private const string ObservationsPath = "/api/v1/observations";
    private const string SourcesPath = "/api/v1/sources";
    private const string ObjectsPath = "/api/v1/catalog/objects";

    private static readonly Guid SecondUserId = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    private static int _areaCounter;

    public Task InitializeAsync() => redis.FlushAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ------------------------------------------------------------------
    // Кэш /map/features
    // ------------------------------------------------------------------

    [Fact]
    public async Task Second_request_served_from_cache()
    {
        var ct = CancellationToken.None;
        CountingMapFeaturesService.Reset();

        await using var api = await CreateApiAsync(
            configure: services =>
            {
                services.RemoveAll<MapFeaturesService>();
                services.AddScoped<MapFeaturesService, CountingMapFeaturesService>();
            });

        var (lon, lat) = NextArea();
        var token = await api.ModeratorTokenAsync(ct);
        var objectId = await CreatePointAsync(api, token, lon, lat, ct);

        var url = FeaturesUrl(AreaAround(lon, lat));

        using var first = await api.GetAsync(url, accessToken: null, ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        var firstBody = await CatalogApi.ReadJsonAsync(first, ct);
        firstBody.GetProperty("items").EnumerateArray()
            .ShouldContain(item => item.GetProperty("id").GetGuid() == objectId);

        using var second = await api.GetAsync(url, accessToken: null, ct);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        var secondBody = await CatalogApi.ReadJsonAsync(second, ct);

        CountingMapFeaturesService.Calls.ShouldBe(1, "второй запрос должен обслуживаться из кэша");
        secondBody.GetProperty("generatedAt").GetDateTimeOffset()
            .ShouldBe(firstBody.GetProperty("generatedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task New_version_invalidates_cache()
    {
        var ct = CancellationToken.None;
        CountingMapFeaturesService.Reset();

        await using var api = await CreateApiAsync(
            configure: services =>
            {
                services.RemoveAll<MapFeaturesService>();
                services.AddScoped<MapFeaturesService, CountingMapFeaturesService>();
            });

        var (lon, lat) = NextArea();
        var token = await api.ModeratorTokenAsync(ct);
        await CreatePointAsync(api, token, lon, lat, ct);

        var url = FeaturesUrl(AreaAround(lon, lat));

        using (var warmup = await api.GetAsync(url, accessToken: null, ct))
        {
            warmup.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var secondObjectId = await CreatePointAsync(api, token, lon + 0.002, lat, ct);

        // Версия карты кэшируется на секунду — ждём, чтобы прочитать новую.
        await Task.Delay(1100, ct);

        using var response = await api.GetAsync(url, accessToken: null, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await CatalogApi.ReadJsonAsync(response, ct);

        CountingMapFeaturesService.Calls.ShouldBe(2, "смена версии должна инвалидировать кэш");
        body.GetProperty("items").EnumerateArray()
            .ShouldContain(item => item.GetProperty("id").GetGuid() == secondObjectId);
    }

    [Fact]
    public async Task Redis_down_features_still_200()
    {
        var ct = CancellationToken.None;

        await using var api = await CreateApiAsync(redisDown: true);

        var (lon, lat) = NextArea();
        var token = await api.ModeratorTokenAsync(ct);
        var objectId = await CreatePointAsync(api, token, lon, lat, ct);

        var stopwatch = Stopwatch.StartNew();
        using var response = await api.GetAsync(FeaturesUrl(AreaAround(lon, lat)), accessToken: null, ct);
        stopwatch.Stop();

        //: недоступный Redis — не ошибка карты. Короткий таймаут + пауза после
        // ошибки не должны добавлять к запросу секунды.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));

        var body = await CatalogApi.ReadJsonAsync(response, ct);
        body.GetProperty("items").EnumerateArray()
            .ShouldContain(item => item.GetProperty("id").GetGuid() == objectId);
    }

    // ------------------------------------------------------------------
    // Rate limit POST /observations
    // ------------------------------------------------------------------

    [Fact]
    public async Task Observation_rate_limit_per_user_429()
    {
        var ct = CancellationToken.None;

        await using var api = await CreateApiAsync(RateLimited());
        var setup = await CreateSetupAsync(api, ct);

        //: лимит на пользователя берётся из global_settings (по умолчанию 20/час).
        await SetUserLimitAsync(api, 1, ct);

        try
        {
            var userToken = await api.UserTokenAsync(ct);

            using (var first = await PostObservationAsync(api, userToken, setup, ct))
            {
                first.StatusCode.ShouldBe(HttpStatusCode.Created);
            }

            using var second = await PostObservationAsync(api, userToken, setup, ct);

            second.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            second.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
            second.Headers.TryGetValues("Retry-After", out var retryAfter).ShouldBeTrue();

            var seconds = int.Parse(retryAfter!.Single(), CultureInfo.InvariantCulture);
            seconds.ShouldBeGreaterThan(0);

            (await CatalogApi.ReadJsonAsync(second, ct)).GetProperty("title").GetString()
                .ShouldBe("Слишком много запросов");
        }
        finally
        {
            await SetUserLimitAsync(api, null, ct);
        }
    }

    [Fact]
    public async Task Rate_limit_per_object_429()
    {
        var ct = CancellationToken.None;

        await using var api = await CreateApiAsync(RateLimited(("ObservationObjectLimit", "2")));
        var setup = await CreateSetupAsync(api, ct);
        var token = await api.ModeratorTokenAsync(ct);

        using (var first = await PostObservationAsync(api, token, setup, ct))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using (var second = await PostObservationAsync(api, token, setup, ct))
        {
            second.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using var third = await PostObservationAsync(api, token, setup, ct);

        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        third.Headers.TryGetValues("Retry-After", out var retryAfter).ShouldBeTrue();
        int.Parse(retryAfter!.Single(), CultureInfo.InvariantCulture).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Rate_limit_per_ip_429()
    {
        var ct = CancellationToken.None;

        await using var api = await CreateApiAsync(RateLimited(("ObservationIpLimit", "2")));
        var setup = await CreateSetupAsync(api, ct);
        var token = await api.ModeratorTokenAsync(ct);

        using (var first = await PostObservationAsync(api, token, setup, ct))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using (var second = await PostObservationAsync(api, token, setup, ct))
        {
            second.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using var third = await PostObservationAsync(api, token, setup, ct);

        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        third.Headers.TryGetValues("Retry-After", out var retryAfter).ShouldBeTrue();
        int.Parse(retryAfter!.Single(), CultureInfo.InvariantCulture).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Rate_limit_counters_independent_per_user()
    {
        var ct = CancellationToken.None;

        await using var api = await CreateApiAsync(RateLimited());
        var setup = await CreateSetupAsync(api, ct);

        await SetUserLimitAsync(api, 1, ct);

        try
        {
            var firstUser = await api.UserTokenAsync(ct);
            var secondUser = await TestAuth.CreateAccessTokenAsync(api.Factory.Services, SecondUserId, ct);

            using (var allowed = await PostObservationAsync(api, firstUser, setup, ct))
            {
                allowed.StatusCode.ShouldBe(HttpStatusCode.Created);
            }

            using (var denied = await PostObservationAsync(api, firstUser, setup, ct))
            {
                denied.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            }

            // У второго пользователя собственный счётчик: лимит первого его не блокирует.
            using var otherUser = await PostObservationAsync(api, secondUser, setup, ct);
            otherUser.StatusCode.ShouldBe(HttpStatusCode.Created);
        }
        finally
        {
            await SetUserLimitAsync(api, null, ct);
        }
    }

    [Fact]
    public async Task Retry_after_header_present()
    {
        var ct = CancellationToken.None;

        await using var api = await CreateApiAsync(
            RateLimited(("ObservationObjectLimit", "1"), ("ObservationObjectWindowSeconds", "30")));
        var setup = await CreateSetupAsync(api, ct);
        var token = await api.ModeratorTokenAsync(ct);

        using (var first = await PostObservationAsync(api, token, setup, ct))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using var second = await PostObservationAsync(api, token, setup, ct);

        second.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        second.Headers.TryGetValues("Retry-After", out var retryAfter).ShouldBeTrue();

        var seconds = int.Parse(retryAfter!.Single(), CultureInfo.InvariantCulture);
        seconds.ShouldBeInRange(1, 30);
    }

    [Fact]
    public async Task Redis_down_rate_limit_falls_back_to_memory()
    {
        var ct = CancellationToken.None;

        await using var api = await CreateApiAsync(
            RateLimited(("ObservationObjectLimit", "1")),
            redisDown: true);
        var setup = await CreateSetupAsync(api, ct);
        var token = await api.ModeratorTokenAsync(ct);

        using (var first = await PostObservationAsync(api, token, setup, ct))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using var second = await PostObservationAsync(api, token, setup, ct);

        //: лимит продолжает работать по счётчикам процесса, а не превращается в 500.
        second.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        second.Headers.TryGetValues("Retry-After", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Limiter_window_slides()
    {
        var ct = CancellationToken.None;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Redis"] = redis.ConnectionString,
            })
            .Build();

        using var connection = new BuildingBlocks.Web.Caching.RedisConnection(
            configuration,
            TimeProvider.System);
        var limiter = new RedisRateLimiter(
            connection,
            new InMemoryRateLimiter(TimeProvider.System),
            TimeProvider.System);

        var key = $"rl:test:{Guid.NewGuid():N}";
        var window = TimeSpan.FromMilliseconds(400);

        (await limiter.TryAcquireAsync(key, limit: 2, window, ct)).Allowed.ShouldBeTrue();
        (await limiter.TryAcquireAsync(key, limit: 2, window, ct)).Allowed.ShouldBeTrue();

        var denied = await limiter.TryAcquireAsync(key, limit: 2, window, ct);
        denied.Allowed.ShouldBeFalse();
        denied.RetryAfter.ShouldBeGreaterThan(TimeSpan.Zero);
        denied.RetryAfterSeconds.ShouldBeGreaterThan(0);

        // Окно скользящее: после истечения самых старых попаданий слот снова свободен.
        await Task.Delay(window + TimeSpan.FromMilliseconds(300), ct);

        (await limiter.TryAcquireAsync(key, limit: 2, window, ct)).Allowed.ShouldBeTrue();
    }

    // ------------------------------------------------------------------
    // Инфраструктура теста
    // ------------------------------------------------------------------

    private async Task<CatalogApi> CreateApiAsync(
        IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configure = null,
        bool redisDown = false)
    {
        var effective = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:Redis"] = redisDown
                ? RedisFixture.UnavailableConnectionString
                : redis.ConnectionString,
        };

        if (settings is not null)
        {
            foreach (var (key, value) in settings)
            {
                effective[key] = value;
            }
        }

        var api = new CatalogApi(fixture, configure, effective);
        await api.InitializeAsync();

        return api;
    }

    private static Dictionary<string, string?> RateLimited(params (string Key, string Value)[] overrides)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["RateLimiting:Enabled"] = "true",
        };

        foreach (var (key, value) in overrides)
        {
            settings[$"RateLimiting:{key}"] = value;
        }

        return settings;
    }

    private static Task SetUserLimitAsync(CatalogApi api, int? limit, CancellationToken ct) =>
        limit is { } value
            ? api.ExecuteAsync(
                $"""
                 insert into global_settings (key, value) values ('observation_rate_per_hour', '{value}')
                 on conflict (key) do update set value = excluded.value
                 """,
                ct)
            : api.ExecuteAsync(
                "delete from global_settings where key = 'observation_rate_per_hour'",
                ct);

    private async Task<Setup> CreateSetupAsync(CatalogApi api, CancellationToken ct)
    {
        var token = await api.ModeratorTokenAsync(ct);
        var suffix = CatalogApi.NewSuffix();

        Guid sourceId;
        using (var response = await api.SendAsync(
            HttpMethod.Post,
            SourcesPath,
            token,
            $$"""
              {"name":"Источник {{suffix}}","sourceType":"telegram_chat",
               "chatId":"@rl_{{suffix}}","timeoutSeconds":3600,"userPrompt":""}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            sourceId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
        }

        Guid statusId;
        using (var response = await api.SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{sourceId}/statuses",
            token,
            $$"""
              {"code":"water_{{suffix}}","name":"Вода есть {{suffix}}",
               "description":"Подача идёт","color":"ok","icon":"drop"}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            statusId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
        }

        var (lon, lat) = NextArea();
        var objectId = await CreateObjectAsync(api, token, lon, lat, [sourceId], ct);

        return new Setup(objectId, sourceId, statusId);
    }

    private static async Task<Guid> CreatePointAsync(
        CatalogApi api,
        string token,
        double lon,
        double lat,
        CancellationToken ct)
    {
        var suffix = CatalogApi.NewSuffix();

        using var response = await api.SendAsync(
            HttpMethod.Post,
            ObjectsPath,
            token,
            $$"""
              {"name":"Точка {{suffix}}","objectTypeId":"00000000-0000-0000-0000-00000000aa01","coverageKind":"point",
               "geometry":{"type":"Point","coordinates":[{{lon.ToString(CultureInfo.InvariantCulture)}},{{lat.ToString(CultureInfo.InvariantCulture)}}]},
               "isActive":true,"sourceIds":[]}
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateObjectAsync(
        CatalogApi api,
        string token,
        double lon,
        double lat,
        IReadOnlyList<Guid> sourceIds,
        CancellationToken ct)
    {
        var suffix = CatalogApi.NewSuffix();
        var links = string.Join(",", sourceIds.Select(id => $"\"{id}\""));

        using var response = await api.SendAsync(
            HttpMethod.Post,
            ObjectsPath,
            token,
            $$"""
              {"name":"Фиксация {{suffix}}","objectTypeId":"00000000-0000-0000-0000-00000000aa01","coverageKind":"point",
               "geometry":{"type":"Point","coordinates":[{{lon.ToString(CultureInfo.InvariantCulture)}},{{lat.ToString(CultureInfo.InvariantCulture)}}]},
               "isActive":true,"sourceIds":[{{links}}]}
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> PostObservationAsync(
        CatalogApi api,
        string token,
        Setup setup,
        CancellationToken ct) =>
        api.SendAsync(
            HttpMethod.Post,
            ObservationsPath,
            token,
            $$"""
              {"mapObjectId":"{{setup.ObjectId}}","sourceId":"{{setup.SourceId}}",
               "sourceStatusId":"{{setup.StatusId}}"}
              """,
            ct);

    private static string FeaturesUrl(string bbox) =>
        $"{FeaturesPath}?bbox={bbox}&zoom=15";

    private static string AreaAround(double lon, double lat) => string.Create(
        CultureInfo.InvariantCulture,
        $"{lon - 0.01},{lat - 0.01},{lon + 0.01},{lat + 0.01}");

    /// <summary>Уникальная координатная площадка на тест: выборка не смешивается с чужими фиксациями.</summary>
    private static (double Lon, double Lat) NextArea()
    {
        var index = Interlocked.Increment(ref _areaCounter);

        return (37.60 + (index * 0.05), 47.90 + (index * 0.05));
    }

    private sealed record Setup(Guid ObjectId, Guid SourceId, Guid StatusId);

    /// <summary>Сервис-счётчик: доказывает, что второй запрос не доходит до выборки из БД.</summary>
    private sealed class CountingMapFeaturesService(MapQueries queries, TimeProvider time)
        : MapFeaturesService(queries, time)
    {
        private static int _calls;

        public static int Calls => Volatile.Read(ref _calls);

        public static void Reset() => Interlocked.Exchange(ref _calls, 0);

        public override async Task<MapFeaturesResponse> GetAsync(
            MapFeaturesQuery query,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);

            return await base.GetAsync(query, ct);
        }
    }
}
