using System.Net;
using System.Text.Json;

using Api.IntegrationTests.Identity;
using Api.IntegrationTests.Infrastructure;

using Catalog.Contracts;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using Shouldly;

namespace Api.IntegrationTests.Catalog;

/// <summary>
/// Фиксации на карте: CRUD, GeoJSON,
/// проверки геометрии, фильтры, привязка источников, аудит, права и триггер
/// <c>updated_at</c>. Хост Api поднимается на общей БД коллекции; изоляция — уникальные
/// названия и коды на каждый тест.
/// </summary>
[Collection("postgres")]
public sealed class ObjectsTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string ObjectsPath = "/api/v1/catalog/objects";

    private CatalogApi _api = null!;

    public async Task InitializeAsync()
    {
        _api = new CatalogApi(fixture);
        await _api.InitializeAsync();
    }

    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task Create_point_and_polygon_returns_geo_json_with_srid_4326()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);

        var pointName = $"Колонка {CatalogApi.NewSuffix()}";
        Guid pointId;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            ObjectsPath,
            token,
            $$"""
              {"name":"{{pointName}}","coverageKind":"point",
               "geometry":{"type":"Point","coordinates":[37.80,48.01]},
               "address":"пр. Ленинский, 12" }
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);

            var json = await CatalogApi.ReadJsonAsync(response, ct);
            pointId = json.GetProperty("id").GetGuid();
            json.GetProperty("name").GetString().ShouldBe(pointName);
            json.GetProperty("coverageKind").GetString().ShouldBe("point");
            json.GetProperty("address").GetString().ShouldBe("пр. Ленинский, 12");
            json.GetProperty("isActive").GetBoolean().ShouldBeTrue();
            json.GetProperty("sourceIds").GetArrayLength().ShouldBe(0);

            var geometry = json.GetProperty("geometry");
            geometry.GetProperty("type").GetString().ShouldBe("Point");
            geometry.GetProperty("coordinates")[0].GetDouble().ShouldBe(37.80);
            geometry.GetProperty("coordinates")[1].GetDouble().ShouldBe(48.01);
        }

        (await _api.CountAsync(
            $"""
             select count(*) from map_objects
             where id = '{pointId}'
               and geometrytype(geometry::geometry) = 'POINT'
               and ST_SRID(geometry::geometry) = 4326
             """,
            ct)).ShouldBe(1L);

        var polygonName = $"Зона {CatalogApi.NewSuffix()}";
        Guid polygonId;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            ObjectsPath,
            token,
            $$"""
              {"name":"{{polygonName}}","coverageKind":"polygon",
               "geometry":{"type":"Polygon","coordinates":[[[37.70,48.00],[37.75,48.00],[37.75,48.05],[37.70,48.05],[37.70,48.00]]]} }
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);

            var json = await CatalogApi.ReadJsonAsync(response, ct);
            polygonId = json.GetProperty("id").GetGuid();
            json.GetProperty("coverageKind").GetString().ShouldBe("polygon");

            var coordinates = json.GetProperty("geometry").GetProperty("coordinates");
            coordinates.GetArrayLength().ShouldBe(1);
            coordinates[0].GetArrayLength().ShouldBe(5);
            coordinates[0][0][0].GetDouble().ShouldBe(37.70);
            coordinates[0][4][0].GetDouble().ShouldBe(coordinates[0][0][0].GetDouble());
        }

        (await _api.CountAsync(
            $"""
             select count(*) from map_objects
             where id = '{polygonId}'
               and geometrytype(geometry::geometry) = 'POLYGON'
               and ST_SRID(geometry::geometry) = 4326
               and ST_Area(geometry) > 0
             """,
            ct)).ShouldBe(1L);

        // Чтение обратно: тот же GeoJSON, что и на входе.
        using (var response = await _api.GetAsync($"{ObjectsPath}/{pointId}", token, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var json = await CatalogApi.ReadJsonAsync(response, ct);
            json.GetProperty("id").GetGuid().ShouldBe(pointId);
            json.GetProperty("geometry").GetProperty("type").GetString().ShouldBe("Point");
            json.GetProperty("geometry").GetProperty("coordinates")[0].GetDouble().ShouldBe(37.80);
        }
    }

    [Fact]
    public async Task Point_coverage_with_polygon_geometry_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            ObjectsPath,
            token,
            """
            {"name":"Точка с полигоном","coverageKind":"point",
             "geometry":{"type":"Polygon","coordinates":[[[37.70,48.00],[37.75,48.00],[37.75,48.05],[37.70,48.00]]]} }
            """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        json.GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "geometry");
    }

    [Fact]
    public async Task Self_intersecting_polygon_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);

        // «Бабочка»: кольцо замкнуто, но стороны пересекаются.
        using var response = await _api.SendAsync(
            HttpMethod.Post,
            ObjectsPath,
            token,
            """
            {"name":"Самопересечение","coverageKind":"polygon",
             "geometry":{"type":"Polygon","coordinates":[[[37.70,48.00],[37.75,48.05],[37.75,48.00],[37.70,48.05],[37.70,48.00]]]} }
            """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        json.GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "geometry");
    }

    [Fact]
    public async Task Coordinates_out_of_range_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            ObjectsPath,
            token,
            """
            {"name":"Вне диапазона","coverageKind":"point",
             "geometry":{"type":"Point","coordinates":[200.0,48.0]} }
            """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        json.GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "geometry");
    }

    [Fact]
    public async Task Point_geometry_kind_type_rejects_polygon_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (objectTypeId, _) = await CreateObjectTypeAsync(token, "point", ct);

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            ObjectsPath,
            token,
            $$"""
              {"name":"Полигон для точки","objectTypeId":"{{objectTypeId}}","coverageKind":"polygon",
               "geometry":{"type":"Polygon","coordinates":[[[37.70,48.00],[37.75,48.00],[37.75,48.05],[37.70,48.00]]]} }
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        json.GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "geometry");
    }

    [Fact]
    public async Task Unknown_object_type_and_group_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            ObjectsPath,
            token,
            $$"""
              {"name":"Неизвестный тип","objectTypeId":"{{Guid.NewGuid()}}","coverageKind":"point",
               "geometry":{"type":"Point","coordinates":[37.80,48.01]} }
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

            var json = await CatalogApi.ReadJsonAsync(response, ct);
            json.GetProperty("errors").EnumerateArray()
                .ShouldContain(error => error.GetProperty("field").GetString() == "objectTypeId");
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            ObjectsPath,
            token,
            $$"""
              {"name":"Неизвестная группа","groupId":"{{Guid.NewGuid()}}","coverageKind":"point",
               "geometry":{"type":"Point","coordinates":[37.80,48.01]} }
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

            var json = await CatalogApi.ReadJsonAsync(response, ct);
            json.GetProperty("errors").EnumerateArray()
                .ShouldContain(error => error.GetProperty("field").GetString() == "groupId");
        }
    }

    [Fact]
    public async Task Q_searches_name_substring_case_insensitive()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var marker = CatalogApi.NewSuffix();
        var firstName = $"ТестВодоканал{marker} Северный";
        var secondName = $"ТЕСТВОДОКАНАЛ{marker} Южный";
        var otherName = $"Котельная {marker}";

        foreach (var name in new[] { firstName, secondName, otherName })
        {
            using var response = await CreatePointAsync(token, name, ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var query = Uri.EscapeDataString($"тестводоканал{marker}");

        using var listResponse = await _api.GetAsync($"{ObjectsPath}?q={query}", token, ct);
        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var page = await CatalogApi.ReadJsonAsync(listResponse, ct);
        var names = page.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString())
            .ToArray();

        names.ShouldContain(firstName);
        names.ShouldContain(secondName);
        names.ShouldNotContain(otherName);
    }

    [Fact]
    public async Task Filter_by_source_and_group()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var sourceId = await _api.CreateSourceAsync(token, ct: ct);
        var groupId = await CreateGroupAsync(token, ct);
        var linkedName = $"С источником {CatalogApi.NewSuffix()}";
        var unlinkedName = $"Без источника {CatalogApi.NewSuffix()}";

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            ObjectsPath,
            token,
            $$"""
              {"name":"{{linkedName}}","groupId":"{{groupId}}","coverageKind":"point",
               "geometry":{"type":"Point","coordinates":[37.80,48.01]},"sourceIds":["{{sourceId}}"]}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using (var response = await CreatePointAsync(token, unlinkedName, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using (var response = await _api.GetAsync($"{ObjectsPath}?sourceId={sourceId}", token, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var page = await CatalogApi.ReadJsonAsync(response, ct);
            var names = page.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("name").GetString())
                .ToArray();

            names.ShouldContain(linkedName);
            names.ShouldNotContain(unlinkedName);
        }

        using (var response = await _api.GetAsync($"{ObjectsPath}?groupId={groupId}", token, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var page = await CatalogApi.ReadJsonAsync(response, ct);
            var items = page.GetProperty("items").EnumerateArray().ToArray();

            items.ShouldNotBeEmpty();
            items.ShouldAllBe(item => item.GetProperty("groupId").GetGuid() == groupId);
        }

        // categoryId фильтрует через тип объекта, objectTypeId — напрямую.
        var (objectTypeId, categoryId) = await CreateObjectTypeAsync(token, "point", ct);
        var typedName = $"С типом {CatalogApi.NewSuffix()}";

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            ObjectsPath,
            token,
            $$"""
              {"name":"{{typedName}}","objectTypeId":"{{objectTypeId}}","coverageKind":"point",
               "geometry":{"type":"Point","coordinates":[37.80,48.01]} }
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        foreach (var filter in new[] { $"categoryId={categoryId}", $"objectTypeId={objectTypeId}" })
        {
            using var response = await _api.GetAsync($"{ObjectsPath}?{filter}", token, ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var page = await CatalogApi.ReadJsonAsync(response, ct);
            var names = page.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("name").GetString())
                .ToArray();

            names.ShouldContain(typedName);
            names.ShouldNotContain(linkedName);
        }
    }

    [Fact]
    public async Task Pagination_returns_next_cursor()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var marker = CatalogApi.NewSuffix();

        foreach (var suffix in new[] { "Первый", "Второй" })
        {
            using var response = await CreatePointAsync(token, $"Страница {suffix} {marker}", ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var query = Uri.EscapeDataString($"Страница");
        string? cursor;

        using (var response = await _api.GetAsync($"{ObjectsPath}?q={query}&limit=1", token, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var page = await CatalogApi.ReadJsonAsync(response, ct);
            page.GetProperty("items").GetArrayLength().ShouldBe(1);

            cursor = page.GetProperty("nextCursor").GetString();
            cursor.ShouldNotBeNullOrWhiteSpace();
        }

        using (var response = await _api.GetAsync($"{ObjectsPath}?q={query}&limit=1&cursor={cursor}", token, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var page = await CatalogApi.ReadJsonAsync(response, ct);
            page.GetProperty("items").GetArrayLength().ShouldBe(1);
        }
    }

    [Fact]
    public async Task Link_two_sources_is_idempotent_and_unlink_works()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var firstSourceId = await _api.CreateSourceAsync(token, ct: ct);
        var secondSourceId = await _api.CreateSourceAsync(token, ct: ct);

        Guid objectId;
        using (var response = await CreatePointAsync(token, $"Два источника {CatalogApi.NewSuffix()}", ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);

            objectId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"{ObjectsPath}/{objectId}/sources/{firstSourceId}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"{ObjectsPath}/{objectId}/sources/{secondSourceId}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // Повторная привязка — тот же 204, без дубля в БД.
        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"{ObjectsPath}/{objectId}/sources/{firstSourceId}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await _api.CountAsync(
            $"select count(*) from map_object_sources where map_object_id = '{objectId}'",
            ct)).ShouldBe(2L);

        using (var response = await _api.GetAsync($"{ObjectsPath}/{objectId}", token, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var sourceIds = (await CatalogApi.ReadJsonAsync(response, ct))
                .GetProperty("sourceIds")
                .EnumerateArray()
                .Select(item => item.GetGuid())
                .ToArray();

            sourceIds.ShouldBe([firstSourceId, secondSourceId], ignoreOrder: true);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Delete,
            $"{ObjectsPath}/{objectId}/sources/{firstSourceId}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await _api.CountAsync(
            $"select count(*) from map_object_sources where map_object_id = '{objectId}'",
            ct)).ShouldBe(1L);

        // Отвязка непривязанного источника — 404 по контракту.
        using (var response = await _api.SendAsync(
            HttpMethod.Delete,
            $"{ObjectsPath}/{objectId}/sources/{firstSourceId}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        var unlinkEntry = await _api.FindAuditAsync(
            "catalog.object.source.unlink",
            objectId.ToString(),
            ct);
        unlinkEntry.ShouldNotBeNull("Отвязка источника должна попасть в аудит.");
    }

    [Fact]
    public async Task Delete_without_observations_204_and_audited()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);

        Guid objectId;
        using (var response = await CreatePointAsync(token, $"На удаление {CatalogApi.NewSuffix()}", ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);

            objectId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Delete,
            $"{ObjectsPath}/{objectId}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await _api.CountAsync($"select count(*) from map_objects where id = '{objectId}'", ct))
            .ShouldBe(0L);

        var deleteEntry = await _api.FindAuditAsync("catalog.object.delete", objectId.ToString(), ct);
        deleteEntry.ShouldNotBeNull("Удаление фиксации должно попасть в аудит.");
        deleteEntry!.ActorId.ShouldBe(CatalogApi.ModeratorId);
        deleteEntry.EntityType.ShouldBe("map_object");
        deleteEntry.After.ShouldBeNull();
    }

    [Fact]
    public async Task Delete_with_observations_409()
    {
        var ct = CancellationToken.None;
        using var factory = new TestApiFactory(fixture.ConnectionString);

        using var overridden = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMapObjectDeletionGuard>();
            services.AddScoped<IMapObjectDeletionGuard>(_ => new AlwaysHasObservationsGuard());
        }));

        using var client = overridden.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });

        using (var health = await client.GetAsync("/health", ct))
        {
            health.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var token = await TestAuth.CreateAccessTokenAsync(overridden.Services, CatalogApi.ModeratorId, ct);

        Guid objectId;
        using (var response = await SendAsync(
            client,
            HttpMethod.Post,
            ObjectsPath,
            token,
            $$"""
              {"name":"С наблюдениями {{CatalogApi.NewSuffix()}}","coverageKind":"point",
               "geometry":{"type":"Point","coordinates":[37.80,48.01]} }
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);

            objectId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
        }

        using (var response = await SendAsync(
            client,
            HttpMethod.Delete,
            $"{ObjectsPath}/{objectId}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

            var json = await CatalogApi.ReadJsonAsync(response, ct);
            json.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("isActive");
        }

        using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync(ct);

            await using var command = new NpgsqlCommand(
                $"select count(*) from map_objects where id = '{objectId}'",
                connection);

            Convert.ToInt64(
                await command.ExecuteScalarAsync(ct),
                System.Globalization.CultureInfo.InvariantCulture).ShouldBe(1L);
        }
    }

    [Fact]
    public async Task Permissions_moderator_allowed_user_forbidden_anonymous_unauthorized()
    {
        var ct = CancellationToken.None;
        var userToken = await _api.UserTokenAsync(ct);
        var moderatorToken = await _api.ModeratorTokenAsync(ct);
        var payload = $$"""
            {"name":"Права {{CatalogApi.NewSuffix()}}","coverageKind":"point",
             "geometry":{"type":"Point","coordinates":[37.80,48.01]} }
            """;

        using (var response = await _api.SendAsync(HttpMethod.Post, ObjectsPath, accessToken: null, json: payload, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var response = await _api.SendAsync(HttpMethod.Post, ObjectsPath, userToken, payload, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var response = await _api.SendAsync(HttpMethod.Post, ObjectsPath, moderatorToken, payload, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        // Объекты не публичны: список тоже требует право.
        using (var response = await _api.GetAsync(ObjectsPath, accessToken: null, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var response = await _api.GetAsync(ObjectsPath, userToken, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task Map_object_directory_returns_sources_and_objects_of_source()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var firstSourceId = await _api.CreateSourceAsync(token, ct: ct);
        var secondSourceId = await _api.CreateSourceAsync(token, ct: ct);
        var name = $"Для справочника {CatalogApi.NewSuffix()}";
        const string address = "пр. Мира, 1";

        Guid objectId;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            ObjectsPath,
            token,
            $$"""
              {"name":"{{name}}","coverageKind":"point","address":"{{address}}",
               "geometry":{"type":"Point","coordinates":[37.80,48.01]},
               "sourceIds":["{{firstSourceId}}","{{secondSourceId}}"]}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);

            objectId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
        }

        await using var scope = _api.Factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<IMapObjectDirectory>();

        var sourceIds = await directory.GetSourceIdsAsync(objectId, ct);
        sourceIds.ShouldBe([firstSourceId, secondSourceId], ignoreOrder: true);

        var objects = await directory.GetObjectsOfSourceAsync(firstSourceId, ct);
        var linked = objects.ShouldHaveSingleItem();
        linked.Id.ShouldBe(objectId);
        linked.Name.ShouldBe(name);
        linked.Address.ShouldBe(address);

        (await directory.ExistsAsync(objectId, ct)).ShouldBeTrue();
        (await directory.ExistsAsync(Guid.NewGuid(), ct)).ShouldBeFalse();

        (await directory.GetSourceIdsAsync(Guid.NewGuid(), ct)).ShouldBeEmpty();
        (await directory.GetObjectsOfSourceAsync(Guid.NewGuid(), ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Updated_at_trigger_updates_column_on_update()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var objectId = Guid.NewGuid();

        // Прямая вставка с заведомо старым updated_at: триггер срабатывает только на UPDATE.
        await _api.ExecuteAsync(
            $"""
             insert into map_objects (id, name, coverage_kind, geometry, created_at, updated_at)
             values ('{objectId}', 'Старая запись', 'point',
                     ST_GeographyFromText('POINT(37.8 48.0)'),
                     '2000-01-01T00:00:00Z', '2000-01-01T00:00:00Z')
             """,
            ct);

        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"{ObjectsPath}/{objectId}",
            token,
            """
            {"name":"Новая запись","coverageKind":"point",
             "geometry":{"type":"Point","coordinates":[37.80,48.01]} }
            """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await _api.CountAsync(
            $"""
             select count(*) from map_objects
             where id = '{objectId}' and updated_at > '2001-01-01T00:00:00Z'
             """,
            ct)).ShouldBe(1L);
    }

    private Task<HttpResponseMessage> CreatePointAsync(
        string token,
        string name,
        CancellationToken ct) =>
        _api.SendAsync(
            HttpMethod.Post,
            ObjectsPath,
            token,
            $$"""
              {"name":"{{name}}","coverageKind":"point",
               "geometry":{"type":"Point","coordinates":[37.80,48.01]} }
              """,
            ct);

    private async Task<(Guid Id, Guid CategoryId)> CreateObjectTypeAsync(
        string token,
        string geometryKind,
        CancellationToken ct)
    {
        var suffix = CatalogApi.NewSuffix();
        var categoryId = await _api.CreateCategoryAsync(
            token,
            $"cat_{suffix}",
            $"Категория {suffix}",
            ct);

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/object-types",
            token,
            $$"""
              {"categoryId":"{{categoryId}}","code":"type_{{suffix}}","name":"Тип {{suffix}}",
               "geometryKind":"{{geometryKind}}"}
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (
            (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid(),
            categoryId);
    }

    private async Task<Guid> CreateGroupAsync(string token, CancellationToken ct)
    {
        using var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/groups",
            token,
            $$"""{"name":"Группа {{CatalogApi.NewSuffix()}}"}""",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string accessToken,
        string? json,
        CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            accessToken);

        if (json is not null)
        {
            request.Content = new StringContent(
                json,
                System.Text.Encoding.UTF8,
                "application/json");
        }

        return client.SendAsync(request, ct);
    }

    /// <summary>
    /// Guard с наблюдениями: реакция endpoint на «у фиксации есть наблюдения» проверяется
    /// подменой реализации <see cref="IMapObjectDeletionGuard"/> в DI, без данных в БД. Реальные
    /// реализации и сквозной 409 с наблюдениями — в
    /// <c>Observations/ObservationStorageTests</c>.
    /// </summary>
    private sealed class AlwaysHasObservationsGuard : IMapObjectDeletionGuard
    {
        public Task<bool> HasObservationsAsync(Guid mapObjectId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            return Task.FromResult(true);
        }
    }
}
