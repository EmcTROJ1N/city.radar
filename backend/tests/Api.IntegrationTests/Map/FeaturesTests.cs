using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using Api.IntegrationTests.Catalog;
using Api.IntegrationTests.Infrastructure;

using Shouldly;

namespace Api.IntegrationTests.Map;

/// <summary>
/// Выборка карты по bbox (016):
/// попадание точки/пересекающегося полигона, отсечение снаружи, проверки bbox и zoom,
/// фильтры kinds/categories/statuses/groups, неактивные фиксации, «неизвестный» статус,
/// поля оформления, упрощение по zoom и анонимный доступ.
/// Хост Api поднимается на общей БД коллекции; изоляция — уникальная координатная площадка
/// на каждый тест и фильтрация по собственному bbox.
/// </summary>
[Collection("postgres")]
public sealed class FeaturesTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string FeaturesPath = "/api/v1/map/features";
    private const string ObjectsPath = "/api/v1/catalog/objects";

    private static int _areaCounter;

    private CatalogApi _api = null!;

    public async Task InitializeAsync()
    {
        _api = new CatalogApi(fixture);
        await _api.InitializeAsync();
    }

    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task Point_inside_bbox_is_returned_and_outside_is_not()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (lon, lat) = NextArea();
        var insideName = $"Колонка {CatalogApi.NewSuffix()}";

        var insideId = await CreatePointAsync(token, lon, lat, insideName, ct: ct);
        var outsideId = await CreatePointAsync(token, lon + 1.0, lat, "Снаружи", ct: ct);

        using var response = await _api.GetAsync(FeaturesUrl(AreaAround(lon, lat), zoom: 15), ct: ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        json.GetProperty("generatedAt").ValueKind.ShouldBe(JsonValueKind.String);

        var items = json.GetProperty("items");
        var inside = items.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == insideId);

        inside.GetProperty("name").GetString().ShouldBe(insideName);
        inside.GetProperty("coverageKind").GetString().ShouldBe("point");
        inside.GetProperty("geometry").GetProperty("type").GetString().ShouldBe("Point");

        items.EnumerateArray()
            .ShouldNotContain(item => item.GetProperty("id").GetGuid() == outsideId);
    }

    [Fact]
    public async Task Polygon_crossing_bbox_without_vertices_inside_is_returned()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (lon, lat) = NextArea();

        // Кольцо вокруг площадки: ни одна вершина не попадает внутрь bbox.
        var ring = new List<(double Lon, double Lat)>
        {
            (lon - 0.2, lat - 0.2),
            (lon + 0.2, lat - 0.2),
            (lon + 0.2, lat + 0.2),
            (lon - 0.2, lat + 0.2),
            (lon - 0.2, lat - 0.2),
        };

        var polygonId = await CreatePolygonAsync(token, ring, $"Зона {CatalogApi.NewSuffix()}", ct);

        using var response = await _api.GetAsync(FeaturesUrl(AreaAround(lon, lat), zoom: 15), ct: ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        var polygon = json.GetProperty("items")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == polygonId);

        polygon.GetProperty("coverageKind").GetString().ShouldBe("polygon");
        polygon.GetProperty("geometry").GetProperty("type").GetString().ShouldBe("Polygon");
    }

    [Fact]
    public async Task Inverted_bbox_400()
    {
        using var response = await _api.GetAsync(
            FeaturesUrl("20,20,10,10", zoom: 10),
            ct: CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Huge_bbox_400()
    {
        using var response = await _api.GetAsync(
            FeaturesUrl("0,0,20,20", zoom: 3),
            ct: CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Malformed_bbox_400()
    {
        using var response = await _api.GetAsync(
            FeaturesUrl("37.6,47.9,38.1", zoom: 10),
            ct: CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Zoom_out_of_range_400()
    {
        var (lon, lat) = NextArea();
        var bbox = AreaAround(lon, lat);

        using (var tooBig = await _api.GetAsync(FeaturesUrl(bbox, zoom: 23), ct: CancellationToken.None))
        {
            tooBig.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var negative = await _api.GetAsync(FeaturesUrl(bbox, zoom: -1), ct: CancellationToken.None))
        {
            negative.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
    }

    [Fact]
    public async Task Kinds_filter_returns_only_requested_geometry()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (lon, lat) = NextArea();

        var pointId = await CreatePointAsync(token, lon, lat, $"Точка {CatalogApi.NewSuffix()}", ct: ct);
        var polygonId = await CreatePolygonAsync(
            token,
            SquareRing(lon + 0.01, lat + 0.01, 0.01),
            $"Полигон {CatalogApi.NewSuffix()}",
            ct);

        var bbox = AreaAround(lon, lat);

        var onlyPolygons = await ReadItemsAsync($"{FeaturesUrl(bbox, zoom: 15)}&kinds=polygon", ct);
        onlyPolygons.ShouldContain(item => item.GetProperty("id").GetGuid() == polygonId);
        onlyPolygons.ShouldNotContain(item => item.GetProperty("id").GetGuid() == pointId);

        var onlyPoints = await ReadItemsAsync($"{FeaturesUrl(bbox, zoom: 15)}&kinds=point", ct);
        onlyPoints.ShouldContain(item => item.GetProperty("id").GetGuid() == pointId);
        onlyPoints.ShouldNotContain(item => item.GetProperty("id").GetGuid() == polygonId);

        var both = await ReadItemsAsync($"{FeaturesUrl(bbox, zoom: 15)}&kinds=point,polygon", ct);
        both.ShouldContain(item => item.GetProperty("id").GetGuid() == pointId);
        both.ShouldContain(item => item.GetProperty("id").GetGuid() == polygonId);
    }

    [Fact]
    public async Task Categories_filter_returns_objects_of_selected_category()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (lon, lat) = NextArea();

        var firstCategoryId = await _api.CreateCategoryAsync(token, ct: ct);
        var secondCategoryId = await _api.CreateCategoryAsync(token, ct: ct);
        var firstTypeId = await CreateObjectTypeAsync(token, firstCategoryId, ct);
        var secondTypeId = await CreateObjectTypeAsync(token, secondCategoryId, ct);

        var firstId = await CreatePointAsync(
            token, lon, lat, $"Первая {CatalogApi.NewSuffix()}", firstTypeId, ct: ct);
        var secondId = await CreatePointAsync(
            token, lon + 0.01, lat + 0.01, $"Вторая {CatalogApi.NewSuffix()}", secondTypeId, ct: ct);

        var items = await ReadItemsAsync(
            $"{FeaturesUrl(AreaAround(lon, lat), zoom: 15)}&categories={firstCategoryId}",
            ct);

        var selected = items.Single(item => item.GetProperty("id").GetGuid() == firstId);
        selected.GetProperty("categoryId").GetGuid().ShouldBe(firstCategoryId);
        selected.GetProperty("objectTypeId").GetGuid().ShouldBe(firstTypeId);

        items.ShouldNotContain(item => item.GetProperty("id").GetGuid() == secondId);
        items.ShouldNotContain(item => item.GetProperty("id").GetGuid() == secondCategoryId);
    }

    [Fact]
    public async Task Statuses_filter_returns_only_selected_status_code()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (lon, lat) = NextArea();
        var suffix = CatalogApi.NewSuffix();

        var (sourceA, statusA) = await CreateSourceStatusAsync(
            token, $"est_{suffix}", $"Есть вода {suffix}", "ok", ct);
        var (sourceB, statusB) = await CreateSourceStatusAsync(
            token, $"net_{suffix}", $"Нет воды {suffix}", "none", ct);

        var firstId = await CreatePointAsync(token, lon, lat, $"Есть {suffix}", ct: ct);
        var secondId = await CreatePointAsync(token, lon + 0.01, lat + 0.01, $"Нет {suffix}", ct: ct);
        await SetCurrentStatusAsync(firstId, sourceA, statusA, ct);
        await SetCurrentStatusAsync(secondId, sourceB, statusB, ct);

        var items = await ReadItemsAsync(
            $"{FeaturesUrl(AreaAround(lon, lat), zoom: 15)}&statuses=est_{suffix}",
            ct);

        items.ShouldContain(item => item.GetProperty("id").GetGuid() == firstId);
        items.ShouldNotContain(item => item.GetProperty("id").GetGuid() == secondId);
    }

    [Fact]
    public async Task Groups_filter_and_none_value()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (lon, lat) = NextArea();
        var groupId = await CreateGroupAsync(token, ct);

        var groupedId = await CreatePointAsync(
            token, lon, lat, $"В группе {CatalogApi.NewSuffix()}", groupId: groupId, ct: ct);
        var ungroupedId = await CreatePointAsync(
            token, lon + 0.01, lat + 0.01, $"Без группы {CatalogApi.NewSuffix()}", ct: ct);

        var bbox = AreaAround(lon, lat);

        var groupedOnly = await ReadItemsAsync($"{FeaturesUrl(bbox, zoom: 15)}&groups={groupId}", ct);
        groupedOnly.ShouldContain(item => item.GetProperty("id").GetGuid() == groupedId);
        groupedOnly.ShouldNotContain(item => item.GetProperty("id").GetGuid() == ungroupedId);

        var noneOnly = await ReadItemsAsync($"{FeaturesUrl(bbox, zoom: 15)}&groups=none", ct);
        noneOnly.ShouldContain(item => item.GetProperty("id").GetGuid() == ungroupedId);
        noneOnly.ShouldNotContain(item => item.GetProperty("id").GetGuid() == groupedId);

        var both = await ReadItemsAsync(
            $"{FeaturesUrl(bbox, zoom: 15)}&groups={groupId},none",
            ct);
        both.ShouldContain(item => item.GetProperty("id").GetGuid() == groupedId);
        both.ShouldContain(item => item.GetProperty("id").GetGuid() == ungroupedId);
    }

    [Fact]
    public async Task Inactive_object_is_not_returned()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (lon, lat) = NextArea();

        var objectId = await CreatePointAsync(
            token, lon, lat, $"Отключённая {CatalogApi.NewSuffix()}", ct: ct);
        var activeId = await CreatePointAsync(
            token, lon + 0.01, lat + 0.01, $"Активная {CatalogApi.NewSuffix()}", ct: ct);

        await _api.ExecuteAsync(
            $"update map_objects set is_active = false where id = '{objectId}'",
            ct);

        var items = await ReadItemsAsync(FeaturesUrl(AreaAround(lon, lat), zoom: 15), ct);

        items.ShouldContain(item => item.GetProperty("id").GetGuid() == activeId);
        items.ShouldNotContain(item => item.GetProperty("id").GetGuid() == objectId);
    }

    [Fact]
    public async Task Unknown_status_for_object_without_observations()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (lon, lat) = NextArea();

        var objectId = await CreatePointAsync(
            token, lon, lat, $"Без наблюдений {CatalogApi.NewSuffix()}", ct: ct);

        var items = await ReadItemsAsync(FeaturesUrl(AreaAround(lon, lat), zoom: 15), ct);
        var feature = items.Single(item => item.GetProperty("id").GetGuid() == objectId);

        // Контракт: status = null означает «неизвестно»; фиксация при этом остаётся на карте.
        feature.GetProperty("status").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Status_style_fields_present()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (lon, lat) = NextArea();
        var suffix = CatalogApi.NewSuffix();

        var (sourceId, statusId) = await CreateSourceStatusAsync(
            token, $"water_{suffix}", $"Вода {suffix}", "ok", ct);
        var objectId = await CreatePointAsync(token, lon, lat, $"Со статусом {suffix}", ct: ct);

        await SetCurrentStatusAsync(objectId, sourceId, statusId, ct, observationCount: 3);

        var items = await ReadItemsAsync(FeaturesUrl(AreaAround(lon, lat), zoom: 15), ct);
        var status = items.Single(item => item.GetProperty("id").GetGuid() == objectId)
            .GetProperty("status");

        status.GetProperty("sourceStatusId").GetGuid().ShouldBe(statusId);
        status.GetProperty("code").GetString().ShouldBe($"water_{suffix}");
        status.GetProperty("name").GetString().ShouldBe($"Вода {suffix}");
        status.GetProperty("color").GetString().ShouldBe("ok");
        status.GetProperty("icon").GetString().ShouldBe("drop");
        status.GetProperty("freshness").GetString().ShouldBe("fresh");
        status.GetProperty("observedAt").ValueKind.ShouldBe(JsonValueKind.String);
        status.GetProperty("sourceType").GetString().ShouldBe("telegram");
        status.GetProperty("observationCount").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task Low_zoom_simplifies_polygon_vertices_count()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (lon, lat) = NextArea();

        // Круг из 200 сегментов: на крупном масштабе все вершины сохраняются.
        var ring = CircleRing(lon, lat, radius: 0.5, segments: 200);
        var objectId = await CreatePolygonAsync(
            token, ring, $"Подробный полигон {CatalogApi.NewSuffix()}", ct);

        var bbox = AreaAround(lon, lat, pad: 0.6);
        var lowZoom = await ReadItemsAsync(FeaturesUrl(bbox, zoom: 3), ct);
        var highZoom = await ReadItemsAsync(FeaturesUrl(bbox, zoom: 18), ct);

        var lowVertices = PolygonVertexCount(
            lowZoom.Single(item => item.GetProperty("id").GetGuid() == objectId)
                .GetProperty("geometry"));
        var highVertices = PolygonVertexCount(
            highZoom.Single(item => item.GetProperty("id").GetGuid() == objectId)
                .GetProperty("geometry"));

        highVertices.ShouldBe(ring.Count);
        lowVertices.ShouldBeLessThan(highVertices);
    }

    [Fact]
    public async Task Anonymous_can_read()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (lon, lat) = NextArea();

        var objectId = await CreatePointAsync(
            token, lon, lat, $"Публичная {CatalogApi.NewSuffix()}", ct: ct);

        using var response = await _api.GetAsync(
            FeaturesUrl(AreaAround(lon, lat), zoom: 15),
            accessToken: null,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var items = await CatalogApi.ReadJsonAsync(response, ct);
        items.GetProperty("items")
            .EnumerateArray()
            .ShouldContain(item => item.GetProperty("id").GetGuid() == objectId);
    }

    private static (double Lon, double Lat) NextArea()
    {
        // Уникальная площадка на тест: 10…16° по обеим осям, вдали от координат других тестов.
        var index = Interlocked.Increment(ref _areaCounter);

        return (10.0 + (index % 30) * 0.2, 10.0 + (index / 30) * 0.2);
    }

    private static string AreaAround(double lon, double lat, double pad = 0.05) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{lon - pad},{lat - pad},{lon + pad},{lat + pad}");

    private static string FeaturesUrl(string bbox, int zoom) =>
        $"{FeaturesPath}?bbox={bbox}&zoom={zoom.ToString(CultureInfo.InvariantCulture)}";

    private static List<(double Lon, double Lat)> SquareRing(
        double lon,
        double lat,
        double radius) =>
    [
        (lon - radius, lat - radius),
        (lon + radius, lat - radius),
        (lon + radius, lat + radius),
        (lon - radius, lat + radius),
        (lon - radius, lat - radius),
    ];

    private static List<(double Lon, double Lat)> CircleRing(
        double lon,
        double lat,
        double radius,
        int segments)
    {
        var ring = new List<(double Lon, double Lat)>(segments + 1);

        for (var index = 0; index < segments; index++)
        {
            var angle = 2.0 * Math.PI * index / segments;
            ring.Add((
                lon + radius * Math.Cos(angle),
                lat + radius * Math.Sin(angle)));
        }

        ring.Add(ring[0]);

        return ring;
    }

    private static int PolygonVertexCount(JsonElement geometry)
    {
        var coordinates = geometry.GetProperty("coordinates");

        return coordinates.GetArrayLength() == 0 ? 0 : coordinates[0].GetArrayLength();
    }

    private async Task<Guid> CreatePointAsync(
        string token,
        double lon,
        double lat,
        string name,
        Guid? objectTypeId = null,
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

        if (objectTypeId is { } typeId)
        {
            body["objectTypeId"] = typeId;
        }

        if (groupId is { } group)
        {
            body["groupId"] = group;
        }

        return await CreateObjectAsync(token, body, ct);
    }

    private async Task<Guid> CreatePolygonAsync(
        string token,
        IReadOnlyList<(double Lon, double Lat)> ring,
        string name,
        CancellationToken ct)
    {
        var coordinates = new JsonArray();
        foreach (var (lon, lat) in ring)
        {
            coordinates.Add(new JsonArray(lon, lat));
        }

        var body = new JsonObject
        {
            ["name"] = name,
            ["objectTypeId"] = PostgresFixture.AnyObjectTypeId,
            ["coverageKind"] = "polygon",
            ["geometry"] = new JsonObject
            {
                ["type"] = "Polygon",
                ["coordinates"] = new JsonArray(coordinates),
            },
        };

        return await CreateObjectAsync(token, body, ct);
    }

    private async Task<Guid> CreateObjectAsync(
        string token,
        JsonObject body,
        CancellationToken ct)
    {
        using var response = await _api.SendAsync(
            HttpMethod.Post,
            ObjectsPath,
            token,
            body.ToJsonString(),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateObjectTypeAsync(
        string token,
        Guid categoryId,
        CancellationToken ct)
    {
        var suffix = CatalogApi.NewSuffix();
        var body = new JsonObject
        {
            ["categoryId"] = categoryId,
            ["code"] = $"type_{suffix}",
            ["name"] = $"Тип {suffix}",
            ["geometryKind"] = "any",
        };

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/object-types",
            token,
            body.ToJsonString(),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateGroupAsync(string token, CancellationToken ct)
    {
        var suffix = CatalogApi.NewSuffix();
        var body = new JsonObject
        {
            ["name"] = $"Группа {suffix}",
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

    private async Task<(Guid SourceId, Guid StatusId)> CreateSourceStatusAsync(
        string token,
        string code,
        string name,
        string color,
        CancellationToken ct)
    {
        var sourceId = await _api.CreateSourceAsync(token, ct: ct);

        var body = new JsonObject
        {
            ["code"] = code,
            ["name"] = name,
            ["description"] = "Подсказка для LLM",
            ["color"] = color,
            ["icon"] = "drop",
        };

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            $"/api/v1/sources/{sourceId}/statuses",
            token,
            body.ToJsonString(),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var statusId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();

        return (sourceId, statusId);
    }

    private Task SetCurrentStatusAsync(
        Guid objectId,
        Guid sourceId,
        Guid statusId,
        CancellationToken ct,
        int observationCount = 1) =>
        _api.ExecuteAsync(
            $"""
             insert into current_statuses
                 (map_object_id, source_status_id, source_id, observed_at, freshness,
                  source_type, observation_count, updated_at)
             values
                 ('{objectId}', '{statusId}', '{sourceId}', now(), 'fresh',
                  'telegram', {observationCount.ToString(CultureInfo.InvariantCulture)}, now())
             on conflict (map_object_id) do update set
                 source_status_id = excluded.source_status_id,
                 source_id = excluded.source_id,
                 observed_at = excluded.observed_at,
                 freshness = excluded.freshness,
                 source_type = excluded.source_type,
                 observation_count = excluded.observation_count,
                 updated_at = excluded.updated_at
             """,
            ct);

    private async Task<JsonElement[]> ReadItemsAsync(string url, CancellationToken ct)
    {
        using var response = await _api.GetAsync(url, accessToken: null, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await CatalogApi.ReadJsonAsync(response, ct);

        return [.. json.GetProperty("items").EnumerateArray()];
    }
}
