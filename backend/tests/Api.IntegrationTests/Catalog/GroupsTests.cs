using System.Net;

using Api.IntegrationTests.Infrastructure;

using Shouldly;

namespace Api.IntegrationTests.Catalog;

/// <summary>
/// Группы объектов: CRUD, публичное чтение, права,
/// дубль имени без учёта регистра, <c>categoryId</c> и аудит. Удаление группы разрешено (204).
/// </summary>
[Collection("postgres")]
public sealed class GroupsTests(PostgresFixture fixture) : IAsyncLifetime
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
        var name = $"Группа {suffix}";

        Guid id;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/groups",
            token,
            $$"""
              {"categoryId":"{{categoryId}}","name":"{{name}}","color":"#3355aa","isVisibleByDefault":false}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);

            var group = await CatalogApi.ReadJsonAsync(response, ct);
            id = group.GetProperty("id").GetGuid();
            group.GetProperty("categoryId").GetGuid().ShouldBe(categoryId);
            group.GetProperty("name").GetString().ShouldBe(name);
            group.GetProperty("color").GetString().ShouldBe("#3355aa");
            group.GetProperty("isVisibleByDefault").GetBoolean().ShouldBeFalse();
        }

        using (var response = await _api.GetAsync("/api/v1/catalog/groups", accessToken: null, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var groups = await CatalogApi.ReadJsonAsync(response, ct);
            groups.EnumerateArray()
                .Single(group => group.GetProperty("id").GetGuid() == id)
                .GetProperty("name").GetString()
                .ShouldBe(name);
        }

        var updatedName = $"{name} обновлена";
        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"/api/v1/catalog/groups/{id}",
            token,
            $$"""{"categoryId":null,"name":"{{updatedName}}","color":null,"isVisibleByDefault":true}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var group = await CatalogApi.ReadJsonAsync(response, ct);
            group.GetProperty("categoryId").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
            group.GetProperty("name").GetString().ShouldBe(updatedName);
            group.GetProperty("color").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
            group.GetProperty("isVisibleByDefault").GetBoolean().ShouldBeTrue();
        }

        var createEntry = await _api.FindAuditAsync("catalog.group.create", id.ToString(), ct);
        createEntry.ShouldNotBeNull("Создание группы должно попасть в аудит.");
        createEntry!.ActorId.ShouldBe(CatalogApi.ModeratorId);
        createEntry.EntityType.ShouldBe("object_group");
        createEntry.Before.ShouldBeNull();
        createEntry.After!.Value.GetProperty("name").GetString().ShouldBe(name);

        var updateEntry = await _api.FindAuditAsync("catalog.group.update", id.ToString(), ct);
        updateEntry.ShouldNotBeNull("Изменение группы должно попасть в аудит.");
        updateEntry!.Before!.Value.GetProperty("name").GetString().ShouldBe(name);
        updateEntry.After!.Value.GetProperty("name").GetString().ShouldBe(updatedName);

        using (var response = await _api.SendAsync(
            HttpMethod.Delete,
            $"/api/v1/catalog/groups/{id}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var deleteEntry = await _api.FindAuditAsync("catalog.group.delete", id.ToString(), ct);
        deleteEntry.ShouldNotBeNull("Удаление группы должно попасть в аудит.");
        deleteEntry!.After.ShouldBeNull();

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

        using (var response = await _api.GetAsync("/api/v1/catalog/groups", accessToken: null, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/groups",
            accessToken: null,
            $$"""{"name":"Группа {{CatalogApi.NewSuffix()}}"}""",
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
        var name = $"Группа {CatalogApi.NewSuffix()}";
        var payload = $$"""{"name":"{{name}}"}""";

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/groups",
            userToken,
            payload,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/groups",
            moderatorToken,
            payload,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }
    }

    [Fact]
    public async Task Duplicate_name_case_insensitive_409()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var suffix = CatalogApi.NewSuffix();
        var name = $"Группа {suffix}";

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/groups",
            token,
            $$"""{"name":"{{name}}"}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/groups",
            token,
            $$"""{"name":"{{name.ToUpperInvariant()}}"}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        }

        (await _api.CountAsync($"select count(*) from object_groups where lower(name) = lower('{name}')", ct))
            .ShouldBe(1L);
    }

    [Fact]
    public async Task Missing_category_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var name = $"Группа {CatalogApi.NewSuffix()}";
        var unknownCategoryId = Guid.NewGuid();

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/groups",
            token,
            $$"""{"categoryId":"{{unknownCategoryId}}","name":"{{name}}"}""",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        json.GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "categoryId");

        (await _api.CountAsync($"select count(*) from object_groups where name = '{name}'", ct))
            .ShouldBe(0L);
    }

    [Fact]
    public async Task Category_referenced_by_group_cannot_be_deleted_409()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var suffix = CatalogApi.NewSuffix();
        var categoryId = await _api.CreateCategoryAsync(token, $"cat_{suffix}", "Категория", ct);
        var name = $"Группа {suffix}";

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/groups",
            token,
            $$"""{"categoryId":"{{categoryId}}","name":"{{name}}"}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Delete,
            $"/api/v1/catalog/categories/{categoryId}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }
    }

    [Fact]
    public async Task Unknown_id_update_and_delete_404()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var unknownId = Guid.NewGuid();

        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"/api/v1/catalog/groups/{unknownId}",
            token,
            """{"name":"Группа"}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Delete,
            $"/api/v1/catalog/groups/{unknownId}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }
}
