using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

using Api.IntegrationTests.Catalog;
using Api.IntegrationTests.Infrastructure;

using MapReadModel.Core;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;
using NpgsqlTypes;

using Observations.Application;

using Shouldly;

namespace Api.IntegrationTests.Map;

/// <summary>
/// ETag и условные запросы карты
/// Review Focus 3). Версия выдачи включает правки фиксаций, групп, статусов источников и
/// проекции <c>current_statuses</c>, а не только <c>max(updated_at)</c> проекции.
/// Хост Api поднимается на общей БД коллекции; изоляция — уникальная координатная площадка
/// на каждый тест, фильтрация по собственному bbox и уникальные имена/коды.
/// </summary>
[Collection("postgres")]
public sealed class ETagTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string FeaturesPath = "/api/v1/map/features";
    private const string ObjectsPath = "/api/v1/catalog/objects";
    private const string SourcesPath = "/api/v1/sources";

    /// <summary>
    /// Больше секундного TTL кэша версии: гарантирует, что следующий запрос перечитает версию
    /// из БД, а не получит значение, закэшированное до правки данных.
    /// </summary>
    private static readonly TimeSpan VersionCacheWait = TimeSpan.FromMilliseconds(1100);

    private static int _areaCounter;

    private CatalogApi _api = null!;

    public async Task InitializeAsync()
    {
        _api = new CatalogApi(fixture);
        await _api.InitializeAsync();
    }

    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task Repeat_request_returns_304()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (lon, lat) = NextArea();
        var url = FeaturesUrl(AreaAround(lon, lat), zoom: 15);

        await CreatePointAsync(token, lon, lat, $"Точка {CatalogApi.NewSuffix()}", ct: ct);

        var etag = await ReadETagAsync(url, ct);

        using (var repeat = await SendFeaturesAsync(url, etag, ct))
        {
            repeat.StatusCode.ShouldBe(HttpStatusCode.NotModified);
            ETagOf(repeat).ShouldBe(etag);
            repeat.Headers.CacheControl?.NoCache.ShouldBeTrue();
            (await repeat.Content.ReadAsStringAsync(ct)).ShouldBeEmpty();
        }

        // Данные не менялись: после истечения кэша версии ответ по-прежнему 304.
        await Task.Delay(VersionCacheWait, ct);

        using var afterExpiry = await SendFeaturesAsync(url, etag, ct);
        afterExpiry.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        ETagOf(afterExpiry).ShouldBe(etag);
    }

    [Fact]
    public async Task New_observation_changes_etag()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (lon, lat) = NextArea();
        var setup = await CreateLinkedSetupAsync(token, lon, lat, ct);
        var url = FeaturesUrl(AreaAround(lon, lat), zoom: 15);

        var before = await ReadETagAsync(url, ct);

        var now = DateTimeOffset.UtcNow;
        await InsertObservationAsync(
            setup.ObjectId,
            setup.SourceId,
            setup.StatusId,
            observedAt: now.AddMinutes(-1),
            expiresAt: now.AddHours(1),
            ct);
        await RecalculateAsync(setup.ObjectId, now, ct);

        var after = await ReadETagAsync(url, ct);

        after.ShouldNotBe(before);

        // Условный запрос со старым тегом получает новое представление, а не 304.
        using var response = await SendFeaturesAsync(url, before, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        ETagOf(response).ShouldBe(after);
    }

    [Fact]
    public async Task Editing_map_object_name_changes_etag()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (lon, lat) = NextArea();
        var objectId = await CreatePointAsync(token, lon, lat, $"До {CatalogApi.NewSuffix()}", ct: ct);

        // Вторая фиксация создаётся позже: её updated_at становится максимумом таблицы.
        // Правка первой не изменит max(updated_at) и count — версия обязана поймать её хешем строк.
        await Task.Delay(20, ct);
        await CreatePointAsync(token, lon + 0.01, lat, $"Соседняя {CatalogApi.NewSuffix()}", ct: ct);

        var url = FeaturesUrl(AreaAround(lon, lat), zoom: 15);

        var before = await ReadETagAsync(url, ct);

        await UpdatePointAsync(token, objectId, $"После {CatalogApi.NewSuffix()}", lon, lat, ct: ct);

        var after = await ReadETagAsync(url, ct);

        after.ShouldNotBe(before);
    }

    [Fact]
    public async Task Editing_map_object_geometry_changes_etag()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (lon, lat) = NextArea();
        var objectId = await CreatePointAsync(
            token,
            lon,
            lat,
            $"Геометрия {CatalogApi.NewSuffix()}",
            ct: ct);
        var url = FeaturesUrl(AreaAround(lon, lat), zoom: 15);

        var before = await ReadETagAsync(url, ct);

        // Новая точка остаётся внутри того же bbox — меняется только геометрия.
        await UpdatePointAsync(token, objectId, $"Геометрия {CatalogApi.NewSuffix()}", lon + 0.01, lat, ct: ct);

        var after = await ReadETagAsync(url, ct);

        after.ShouldNotBe(before);
    }

    [Fact]
    public async Task Deleting_object_changes_etag()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (lon, lat) = NextArea();
        var objectId = await CreatePointAsync(token, lon, lat, $"Удаляемая {CatalogApi.NewSuffix()}", ct: ct);
        var url = FeaturesUrl(AreaAround(lon, lat), zoom: 15);

        var before = await ReadETagAsync(url, ct);

        using (var response = await _api.SendAsync(
            HttpMethod.Delete,
            $"{ObjectsPath}/{objectId}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var after = await ReadETagAsync(url, ct);

        after.ShouldNotBe(before);
    }

    [Fact]
    public async Task Changing_group_changes_etag()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (lon, lat) = NextArea();
        var firstGroup = await CreateGroupAsync(token, ct);
        var secondGroup = await CreateGroupAsync(token, ct);
        var objectId = await CreatePointAsync(
            token,
            lon,
            lat,
            $"Групповая {CatalogApi.NewSuffix()}",
            groupId: firstGroup,
            ct: ct);
        var url = FeaturesUrl(AreaAround(lon, lat), zoom: 15);

        var before = await ReadETagAsync(url, ct);

        await UpdatePointAsync(
            token,
            objectId,
            $"Групповая {CatalogApi.NewSuffix()}",
            lon,
            lat,
            groupId: secondGroup,
            ct: ct);

        var after = await ReadETagAsync(url, ct);

        after.ShouldNotBe(before);
    }

    [Fact]
    public async Task Changing_source_status_color_changes_etag()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (lon, lat) = NextArea();
        var setup = await CreateLinkedSetupAsync(token, lon, lat, ct);
        var url = FeaturesUrl(AreaAround(lon, lat), zoom: 15);

        var now = DateTimeOffset.UtcNow;
        await InsertObservationAsync(
            setup.ObjectId,
            setup.SourceId,
            setup.StatusId,
            observedAt: now.AddMinutes(-1),
            expiresAt: now.AddHours(1),
            ct);
        await RecalculateAsync(setup.ObjectId, now, ct);

        var before = await ReadETagAsync(url, ct);

        // У source_statuses нет updated_at: изменение цвета/иконки должно попадать в версию
        // через хеш строк, иначе карта останется с устаревшим оформлением.
        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"{SourcesPath}/{setup.SourceId}/statuses/{setup.StatusId}",
            token,
            $$"""
              {"code":"{{setup.StatusCode}}","name":"{{setup.StatusName}}","description":"Подача идёт",
               "color":"none","icon":"alert","sortOrder":1}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var after = await ReadETagAsync(url, ct);

        after.ShouldNotBe(before);
    }

    [Fact]
    public async Task Different_query_gives_different_etag()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (lon, lat) = NextArea();

        await CreatePointAsync(token, lon, lat, $"Запрос {CatalogApi.NewSuffix()}", ct: ct);

        var detailed = await ReadETagAsync(FeaturesUrl(AreaAround(lon, lat), zoom: 15), ct);
        var overview = await ReadETagAsync(FeaturesUrl(AreaAround(lon, lat), zoom: 14), ct);

        detailed.ShouldNotBe(overview);
    }

    [Fact]
    public async Task Same_query_with_reordered_params_gives_same_etag()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (lon, lat) = NextArea();

        await CreatePointAsync(token, lon, lat, $"Порядок {CatalogApi.NewSuffix()}", ct: ct);

        var area = AreaAround(lon, lat);
        var firstCategory = Guid.NewGuid();
        var secondCategory = Guid.NewGuid();
        var firstGroup = Guid.NewGuid();
        var secondGroup = Guid.NewGuid();

        var first = await ReadETagAsync(
            $"{FeaturesPath}?bbox={area}&zoom=15"
            + $"&categories={firstCategory},{secondCategory}"
            + "&statuses=ok,none&kinds=point,polygon"
            + $"&groups={firstGroup},{secondGroup}",
            ct);

        var second = await ReadETagAsync(
            $"{FeaturesPath}?bbox={area}&zoom=15"
            + $"&categories={secondCategory},{firstCategory}"
            + "&statuses=none,ok&kinds=polygon,point"
            + $"&groups={secondGroup},{firstGroup}",
            ct);

        second.ShouldBe(first);
    }

    [Fact]
    public async Task Weak_etag_comparison_and_star_and_list_in_If_None_Match()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (lon, lat) = NextArea();

        await CreatePointAsync(token, lon, lat, $"Слабый тег {CatalogApi.NewSuffix()}", ct: ct);
        var url = FeaturesUrl(AreaAround(lon, lat), zoom: 15);
        var etag = await ReadETagAsync(url, ct);

        // Сильный вариант того же тега: weak comparison игнорирует W/.
        var strong = etag[2..];

        foreach (var candidate in new[] { strong, $"W/\"чужой\", {etag}", "*" })
        {
            using var response = await SendFeaturesAsync(url, candidate, ct);

            response.StatusCode.ShouldBe(HttpStatusCode.NotModified, $"If-None-Match: {candidate}");
            ETagOf(response).ShouldBe(etag);
        }

        using var mismatch = await SendFeaturesAsync(url, "W/\"чужой\"", ct);
        mismatch.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Bad_request_has_no_etag()
    {
        using var response = await SendFeaturesAsync(
            $"{FeaturesPath}?bbox=20,20,10,10&zoom=10",
            ifNoneMatch: null,
            CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Headers.Contains("ETag").ShouldBeFalse();
        response.Headers.ETag.ShouldBeNull();
    }

    [Fact]
    public async Task Not_modified_does_not_query_features()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (lon, lat) = NextArea();

        await CreatePointAsync(token, lon, lat, $"Без выборки {CatalogApi.NewSuffix()}", ct: ct);
        var url = FeaturesUrl(AreaAround(lon, lat), zoom: 15);
        var etag = await ReadETagAsync(url, ct);

        await using var factory = new TestApiFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<MapFeaturesService>();
                services.AddScoped<MapFeaturesService, CountingMapFeaturesService>();
            });

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });

        using (var health = await client.GetAsync("/health", ct))
        {
            health.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        CountingMapFeaturesService.Reset();

        using (var notModified = await SendFeaturesAsync(client, url, etag, ct))
        {
            notModified.StatusCode.ShouldBe(HttpStatusCode.NotModified);
            CountingMapFeaturesService.Calls.ShouldBe(0, "304 не должен доходить до выборки фиксаций");
        }

        using (var ok = await SendFeaturesAsync(client, url, ifNoneMatch: null, ct))
        {
            ok.StatusCode.ShouldBe(HttpStatusCode.OK);
            CountingMapFeaturesService.Calls.ShouldBe(1);
        }
    }

    [Fact]
    public async Task Unchanged_projection_update_keeps_etag()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (lon, lat) = NextArea();
        var setup = await CreateLinkedSetupAsync(token, lon, lat, ct);
        var url = FeaturesUrl(AreaAround(lon, lat), zoom: 15);

        var now = DateTimeOffset.UtcNow;
        await InsertObservationAsync(
            setup.ObjectId,
            setup.SourceId,
            setup.StatusId,
            observedAt: now.AddMinutes(-1),
            expiresAt: now.AddHours(1),
            ct);
        await RecalculateAsync(setup.ObjectId, now, ct);

        var before = await ReadETagAsync(url, ct);

        // «Плановый» пересчёт при неизменном итоге не должен трогать updated_at проекции,
        // иначе ETag прыгал бы на каждом проходе фоновой задачи.
        await Task.Delay(VersionCacheWait, ct);
        await RecalculateAsync(setup.ObjectId, now, ct);

        var after = await ReadETagAsync(url, ct);
        after.ShouldBe(before);

        using var response = await SendFeaturesAsync(url, before, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task Version_is_cached_one_second()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var source = new CountingVersionSource("v1");
        var provider = new MapVersionProvider(source, time);

        (await provider.GetVersionAsync()).ShouldBe("v1");
        source.Reads.ShouldBe(1);

        // В пределах секунды версия берётся из памяти, повторного SQL нет.
        time.Advance(TimeSpan.FromMilliseconds(500));
        (await provider.GetVersionAsync()).ShouldBe("v1");
        source.Reads.ShouldBe(1);

        // Кэш истёк: параллельные запросы выполняют ровно одну загрузку.
        time.Advance(TimeSpan.FromMilliseconds(600));
        source.Version = "v2";

        var parallel = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => provider.GetVersionAsync()));

        parallel.ShouldAllBe(version => version == "v2");
        source.Reads.ShouldBe(2);

        // И следующее истечение срока перечитывает версию снова.
        time.Advance(TimeSpan.FromSeconds(1.1));
        source.Version = "v3";

        (await provider.GetVersionAsync()).ShouldBe("v3");
        source.Reads.ShouldBe(3);
    }

    private static (double Lon, double Lat) NextArea()
    {
        // Уникальная площадка на тест: 20…26° по обеим осям, вдали от координат других тестов.
        var index = Interlocked.Increment(ref _areaCounter);

        return (20.0 + (index % 30) * 0.2, 20.0 + (index / 30) * 0.2);
    }

    private static string AreaAround(double lon, double lat, double pad = 0.05) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{lon - pad},{lat - pad},{lon + pad},{lat + pad}");

    private static string FeaturesUrl(string bbox, int zoom) =>
        $"{FeaturesPath}?bbox={bbox}&zoom={zoom.ToString(CultureInfo.InvariantCulture)}";

    private static string? ETagOf(HttpResponseMessage response) => response.Headers.ETag?.ToString();

    private Task<HttpResponseMessage> SendFeaturesAsync(
        string url,
        string? ifNoneMatch,
        CancellationToken ct) =>
        SendFeaturesAsync(_api.Client, url, ifNoneMatch, ct);

    private static async Task<HttpResponseMessage> SendFeaturesAsync(
        HttpClient client,
        string url,
        string? ifNoneMatch,
        CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (!string.IsNullOrWhiteSpace(ifNoneMatch))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        return await client.SendAsync(request, ct);
    }

    /// <summary>
    /// Читает ETag ответа 200. Перед запросом ждёт истечения секундного кэша версии, чтобы
    /// сравнивать теги, посчитанные по актуальному состоянию БД, а не по предыдущему снимку.
    /// </summary>
    private async Task<string> ReadETagAsync(string url, CancellationToken ct)
    {
        await Task.Delay(VersionCacheWait, ct);

        using var response = await SendFeaturesAsync(url, ifNoneMatch: null, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoCache.ShouldBeTrue();

        var etag = response.Headers.ETag.ShouldNotBeNull("Ответ 200 обязан содержать ETag.");

        return etag.ToString();
    }

    private async Task<Guid> CreatePointAsync(
        string token,
        double lon,
        double lat,
        string name,
        Guid? groupId = null,
        Guid? sourceId = null,
        CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["name"] = name,
            ["objectTypeId"] = PostgresFixture.AnyObjectTypeId,
            ["coverageKind"] = "point",
            ["geometry"] = new JsonObject
            {
                ["type"] = "Point",
                ["coordinates"] = new JsonArray(lon, lat),
            },
        };

        if (groupId is { } group)
        {
            body["groupId"] = group;
        }

        if (sourceId is { } source)
        {
            body["sourceIds"] = new JsonArray(JsonValue.Create(source));
        }

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            ObjectsPath,
            token,
            body.ToJsonString(),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
    }

    private async Task UpdatePointAsync(
        string token,
        Guid objectId,
        string name,
        double lon,
        double lat,
        Guid? groupId = null,
        CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["name"] = name,
            ["objectTypeId"] = PostgresFixture.AnyObjectTypeId,
            ["coverageKind"] = "point",
            ["geometry"] = new JsonObject
            {
                ["type"] = "Point",
                ["coordinates"] = new JsonArray(lon, lat),
            },
        };

        if (groupId is { } group)
        {
            body["groupId"] = group;
        }

        using var response = await _api.SendAsync(
            HttpMethod.Put,
            $"{ObjectsPath}/{objectId}",
            token,
            body.ToJsonString(),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<Guid> CreateGroupAsync(string token, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["name"] = $"Группа {CatalogApi.NewSuffix()}",
        };

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/groups",
            token,
            body.ToJsonString(),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
    }

    private async Task<Setup> CreateLinkedSetupAsync(
        string token,
        double lon,
        double lat,
        CancellationToken ct)
    {
        var suffix = CatalogApi.NewSuffix();
        var sourceId = await _api.CreateSourceAsync(token, ct: ct);
        var code = $"water_{suffix}";
        var statusName = $"Вода {suffix}";

        Guid statusId;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{sourceId}/statuses",
            token,
            $$"""
              {"code":"{{code}}","name":"{{statusName}}","description":"Подача идёт",
               "color":"ok","icon":"drop"}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            statusId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
        }

        var objectId = await CreatePointAsync(
            token,
            lon,
            lat,
            $"Фиксация {suffix}",
            sourceId: sourceId,
            ct: ct);

        return new Setup(sourceId, statusId, objectId, code, statusName);
    }

    private async Task RecalculateAsync(Guid mapObjectId, DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = _api.Factory.Services.CreateAsyncScope();
        var calculator = scope.ServiceProvider.GetRequiredService<IStatusRecalculator>();

        await calculator.RecalculateAsync(mapObjectId, now, ct);
    }

    private async Task InsertObservationAsync(
        Guid mapObjectId,
        Guid sourceId,
        Guid sourceStatusId,
        DateTimeOffset observedAt,
        DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            insert into observations
              (map_object_id, source_id, source_status_id, source_type, reporter_id,
               observed_at, received_at, expires_at)
            values
              (@map_object_id, @source_id, @source_status_id, 'manual', @reporter_id,
               @observed_at, @received_at, @expires_at)
            """,
            connection);

        command.Parameters.Add(new NpgsqlParameter("map_object_id", NpgsqlDbType.Uuid) { Value = mapObjectId });
        command.Parameters.Add(new NpgsqlParameter("source_id", NpgsqlDbType.Uuid) { Value = sourceId });
        command.Parameters.Add(new NpgsqlParameter("source_status_id", NpgsqlDbType.Uuid) { Value = sourceStatusId });
        command.Parameters.Add(new NpgsqlParameter("reporter_id", NpgsqlDbType.Uuid) { Value = CatalogApi.ModeratorId });
        command.Parameters.Add(new NpgsqlParameter("observed_at", NpgsqlDbType.TimestampTz) { Value = observedAt });
        command.Parameters.Add(new NpgsqlParameter("received_at", NpgsqlDbType.TimestampTz) { Value = observedAt });
        command.Parameters.Add(new NpgsqlParameter("expires_at", NpgsqlDbType.TimestampTz) { Value = expiresAt });

        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed record Setup(
        Guid SourceId,
        Guid StatusId,
        Guid ObjectId,
        string StatusCode,
        string StatusName);

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

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class CountingVersionSource(string version) : IMapVersionSource
    {
        private int _reads;

        public string Version { get; set; } = version;

        public int Reads => Volatile.Read(ref _reads);

        public async Task<string> ReadAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _reads);

            // Даём остальным параллельным вызовам дойти до семафора, а не увидеть кэш раньше.
            await Task.Yield();

            return Version;
        }
    }
}
