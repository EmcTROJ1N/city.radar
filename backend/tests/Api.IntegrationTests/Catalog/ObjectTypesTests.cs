using System.Net;
using System.Text.Json;

using Api.IntegrationTests.Infrastructure;

using Shouldly;

namespace Api.IntegrationTests.Catalog;

/// <summary>
/// Типы объектов каталога: CRUD, публичное чтение, права,
/// дубль кода, <c>categoryId</c>, <c>geometryKind</c> и аудит.
/// </summary>
[Collection("postgres")]
public sealed class ObjectTypesTests(PostgresFixture fixture) : IAsyncLifetime
{
    private CatalogApi _api = null!;

    public async Task InitializeAsync()
    {
        _api = new CatalogApi(fixture);
        await _api.InitializeAsync();
    }

    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task Create_read_update_delete_happy_path_and_audit()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var suffix = CatalogApi.NewSuffix();
        var categoryId = await _api.CreateCategoryAsync(token, $"cat_{suffix}", $"Категория {suffix}", ct);
        var code = $"type_{suffix}";
        var name = $"Тип {suffix}";

        Guid id;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/object-types",
            token,
            $$"""
              {"categoryId":"{{categoryId}}","code":"{{code}}","name":"{{name}}",
               "geometryKind":"polygon"}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);

            var objectType = await CatalogApi.ReadJsonAsync(response, ct);
            id = objectType.GetProperty("id").GetGuid();
            objectType.GetProperty("categoryId").GetGuid().ShouldBe(categoryId);
            objectType.GetProperty("code").GetString().ShouldBe(code);
            objectType.GetProperty("name").GetString().ShouldBe(name);
            objectType.GetProperty("geometryKind").GetString().ShouldBe("polygon");
        }

        using (var response = await _api.GetAsync("/api/v1/catalog/object-types", accessToken: null, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var objectTypes = await CatalogApi.ReadJsonAsync(response, ct);
            objectTypes.EnumerateArray()
                .Single(objectType => objectType.GetProperty("id").GetGuid() == id)
                .GetProperty("code").GetString()
                .ShouldBe(code);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"/api/v1/catalog/object-types/{id}",
            token,
            $$"""
              {"categoryId":"{{categoryId}}","code":"{{code}}","name":"{{name}} обновлён",
               "geometryKind":"point"}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var objectType = await CatalogApi.ReadJsonAsync(response, ct);
            objectType.GetProperty("name").GetString().ShouldBe($"{name} обновлён");
            objectType.GetProperty("geometryKind").GetString().ShouldBe("point");
        }

        var createEntry = await _api.FindAuditAsync("catalog.objecttype.create", id.ToString(), ct);
        createEntry.ShouldNotBeNull("Создание типа объекта должно попасть в аудит.");
        createEntry!.ActorId.ShouldBe(CatalogApi.ModeratorId);
        createEntry.EntityType.ShouldBe("object_type");
        createEntry.Before.ShouldBeNull();
        createEntry.After!.Value.GetProperty("code").GetString().ShouldBe(code);

        var updateEntry = await _api.FindAuditAsync("catalog.objecttype.update", id.ToString(), ct);
        updateEntry.ShouldNotBeNull("Изменение типа объекта должно попасть в аудит.");
        updateEntry!.Before!.Value.GetProperty("geometryKind").GetString().ShouldBe("polygon");
        updateEntry.After!.Value.GetProperty("geometryKind").GetString().ShouldBe("point");

        using (var response = await _api.SendAsync(
            HttpMethod.Delete,
            $"/api/v1/catalog/object-types/{id}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var deleteEntry = await _api.FindAuditAsync("catalog.objecttype.delete", id.ToString(), ct);
        deleteEntry.ShouldNotBeNull("Удаление типа объекта должно попасть в аудит.");
        deleteEntry!.After.ShouldBeNull();
        deleteEntry.Before!.Value.GetProperty("name").GetString().ShouldBe($"{name} обновлён");

        using (var response = await _api.SendAsync(
            HttpMethod.Delete,
            $"/api/v1/catalog/categories/{categoryId}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }
    }

    [Fact]
    public async Task Anonymous_can_read_but_cannot_write()
    {
        var ct = CancellationToken.None;

        using (var response = await _api.GetAsync("/api/v1/catalog/object-types", accessToken: null, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/object-types",
            accessToken: null,
            """{"categoryId":"00000000-0000-0000-0000-000000000001","code":"type_anon","name":"Тип"}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        }
    }

    [Fact]
    public async Task User_forbidden_moderator_has_permission()
    {
        var ct = CancellationToken.None;
        var userToken = await _api.UserTokenAsync(ct);
        var moderatorToken = await _api.ModeratorTokenAsync(ct);
        var suffix = CatalogApi.NewSuffix();
        var categoryId = await _api.CreateCategoryAsync(
            moderatorToken,
            $"cat_{suffix}",
            $"Категория {suffix}",
            ct);
        var payload = $$"""
            {"categoryId":"{{categoryId}}","code":"type_{{suffix}}","name":"Тип"}
            """;

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/object-types",
            userToken,
            payload,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/object-types",
            moderatorToken,
            payload,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }
    }

    [Fact]
    public async Task Duplicate_code_409()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var suffix = CatalogApi.NewSuffix();
        var categoryId = await _api.CreateCategoryAsync(token, $"cat_{suffix}", "Категория", ct);
        var code = $"type_{suffix}";

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/object-types",
            token,
            $$"""{"categoryId":"{{categoryId}}","code":"{{code}}","name":"Первый"}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/object-types",
            token,
            $$"""{"categoryId":"{{categoryId}}","code":"{{code}}","name":"Второй"}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        }
    }

    [Fact]
    public async Task Missing_category_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var code = $"type_{CatalogApi.NewSuffix()}";
        var unknownCategoryId = Guid.NewGuid();

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/object-types",
            token,
            $$"""{"categoryId":"{{unknownCategoryId}}","code":"{{code}}","name":"Тип"}""",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        json.GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "categoryId");

        (await _api.CountAsync($"select count(*) from object_types where code = '{code}'", ct))
            .ShouldBe(0L);
    }

    [Fact]
    public async Task Invalid_geometry_kind_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var suffix = CatalogApi.NewSuffix();
        var categoryId = await _api.CreateCategoryAsync(token, $"cat_{suffix}", "Категория", ct);
        var code = $"type_{suffix}";

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/object-types",
            token,
            $$"""
              {"categoryId":"{{categoryId}}","code":"{{code}}","name":"Тип","geometryKind":"line"}
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        json.GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "geometryKind");
    }

    [Fact]
    public async Task List_filters_by_category()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var suffix = CatalogApi.NewSuffix();
        var firstCategoryId = await _api.CreateCategoryAsync(token, $"cat_a_{suffix}", "Первая", ct);
        var secondCategoryId = await _api.CreateCategoryAsync(token, $"cat_b_{suffix}", "Вторая", ct);

        var firstCode = $"type_a_{suffix}";
        var secondCode = $"type_b_{suffix}";

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/object-types",
            token,
            $$"""{"categoryId":"{{firstCategoryId}}","code":"{{firstCode}}","name":"Первый"}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/object-types",
            token,
            $$"""{"categoryId":"{{secondCategoryId}}","code":"{{secondCode}}","name":"Второй"}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using var filtered = await _api.GetAsync(
            $"/api/v1/catalog/object-types?categoryId={firstCategoryId}",
            accessToken: null,
            ct);

        filtered.StatusCode.ShouldBe(HttpStatusCode.OK);

        var objectTypes = await CatalogApi.ReadJsonAsync(filtered, ct);
        var codes = objectTypes.EnumerateArray()
            .Select(objectType => objectType.GetProperty("code").GetString())
            .ToArray();

        codes.ShouldContain(firstCode);
        codes.ShouldNotContain(secondCode);
    }

    [Fact]
    public async Task Unknown_id_update_and_delete_404()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var unknownId = Guid.NewGuid();

        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"/api/v1/catalog/object-types/{unknownId}",
            token,
            $$"""{"categoryId":"{{Guid.NewGuid()}}","code":"type_unknown","name":"Тип"}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Delete,
            $"/api/v1/catalog/object-types/{unknownId}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }
}
