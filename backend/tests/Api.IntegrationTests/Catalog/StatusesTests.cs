using System.Net;

using Api.IntegrationTests.Infrastructure;

using Shouldly;

namespace Api.IntegrationTests.Catalog;

/// <summary>
/// Справочник статусов: CRUD, публичное чтение, права,
/// дубль кода, палитра цветов, <c>categoryId</c> и аудит.
/// </summary>
[Collection("postgres")]
public sealed class StatusesTests(PostgresFixture fixture) : IAsyncLifetime
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
        var code = $"status_{suffix}";
        var name = $"Статус {suffix}";

        Guid id;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/statuses",
            token,
            $$"""
              {"categoryId":"{{categoryId}}","code":"{{code}}","name":"{{name}}",
               "color":"limited","icon":"alert-triangle","severity":5,"isPositive":false}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);

            var status = await CatalogApi.ReadJsonAsync(response, ct);
            id = status.GetProperty("id").GetGuid();
            status.GetProperty("categoryId").GetGuid().ShouldBe(categoryId);
            status.GetProperty("code").GetString().ShouldBe(code);
            status.GetProperty("name").GetString().ShouldBe(name);
            status.GetProperty("color").GetString().ShouldBe("limited");
            status.GetProperty("icon").GetString().ShouldBe("alert-triangle");
            status.GetProperty("severity").GetInt32().ShouldBe(5);
            status.GetProperty("isPositive").GetBoolean().ShouldBeFalse();
        }

        using (var response = await _api.GetAsync("/api/v1/catalog/statuses", accessToken: null, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var statuses = await CatalogApi.ReadJsonAsync(response, ct);
            statuses.EnumerateArray()
                .Single(status => status.GetProperty("id").GetGuid() == id)
                .GetProperty("color").GetString()
                .ShouldBe("limited");
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"/api/v1/catalog/statuses/{id}",
            token,
            $$"""
              {"categoryId":null,"code":"{{code}}","name":"{{name}} обновлён",
               "color":"ok","icon":"check","severity":0,"isPositive":true}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var status = await CatalogApi.ReadJsonAsync(response, ct);
            status.GetProperty("categoryId").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
            status.GetProperty("name").GetString().ShouldBe($"{name} обновлён");
            status.GetProperty("color").GetString().ShouldBe("ok");
            status.GetProperty("isPositive").GetBoolean().ShouldBeTrue();
        }

        var createEntry = await _api.FindAuditAsync("catalog.status.create", id.ToString(), ct);
        createEntry.ShouldNotBeNull("Создание статуса должно попасть в аудит.");
        createEntry!.ActorId.ShouldBe(CatalogApi.ModeratorId);
        createEntry.EntityType.ShouldBe("status_definition");
        createEntry.Before.ShouldBeNull();
        createEntry.After!.Value.GetProperty("color").GetString().ShouldBe("limited");

        var updateEntry = await _api.FindAuditAsync("catalog.status.update", id.ToString(), ct);
        updateEntry.ShouldNotBeNull("Изменение статуса должно попасть в аудит.");
        updateEntry!.Before!.Value.GetProperty("color").GetString().ShouldBe("limited");
        updateEntry.After!.Value.GetProperty("color").GetString().ShouldBe("ok");

        using (var response = await _api.SendAsync(
            HttpMethod.Delete,
            $"/api/v1/catalog/statuses/{id}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var deleteEntry = await _api.FindAuditAsync("catalog.status.delete", id.ToString(), ct);
        deleteEntry.ShouldNotBeNull("Удаление статуса должно попасть в аудит.");
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

        using (var response = await _api.GetAsync("/api/v1/catalog/statuses", accessToken: null, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/statuses",
            accessToken: null,
            """{"code":"status_anon","name":"Статус","color":"ok","icon":"check"}""",
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
        var code = $"status_{CatalogApi.NewSuffix()}";
        var payload = $$"""{"code":"{{code}}","name":"Статус","color":"ok","icon":"check"}""";

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/statuses",
            userToken,
            payload,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/statuses",
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
        var code = $"status_{CatalogApi.NewSuffix()}";

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/statuses",
            token,
            $$"""{"code":"{{code}}","name":"Первый","color":"ok","icon":"check"}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/statuses",
            token,
            $$"""{"code":"{{code}}","name":"Второй","color":"none","icon":"cross"}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }
    }

    [Fact]
    public async Task Invalid_color_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var code = $"status_{CatalogApi.NewSuffix()}";

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/statuses",
            token,
            $$"""{"code":"{{code}}","name":"Статус","color":"#ff0000","icon":"check"}""",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        json.GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "color");

        (await _api.CountAsync($"select count(*) from status_definitions where code = '{code}'", ct))
            .ShouldBe(0L);
    }

    [Fact]
    public async Task Missing_category_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var code = $"status_{CatalogApi.NewSuffix()}";
        var unknownCategoryId = Guid.NewGuid();

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/statuses",
            token,
            $$"""
              {"categoryId":"{{unknownCategoryId}}","code":"{{code}}","name":"Статус",
               "color":"ok","icon":"check"}
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        json.GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "categoryId");

        (await _api.CountAsync($"select count(*) from status_definitions where code = '{code}'", ct))
            .ShouldBe(0L);
    }

    [Fact]
    public async Task Unknown_id_update_and_delete_404()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var unknownId = Guid.NewGuid();

        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"/api/v1/catalog/statuses/{unknownId}",
            token,
            """{"code":"status_unknown","name":"Статус","color":"ok","icon":"check"}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Delete,
            $"/api/v1/catalog/statuses/{unknownId}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }
}
