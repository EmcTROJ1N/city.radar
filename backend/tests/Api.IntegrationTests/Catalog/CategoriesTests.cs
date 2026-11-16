using System.Net;
using System.Text.Json;

using Api.IntegrationTests.Infrastructure;

using Npgsql;

using Shouldly;

namespace Api.IntegrationTests.Catalog;

/// <summary>
/// Категории каталога: CRUD, публичное чтение, права,
/// дубль кода, запрет удаления используемой категории и аудит каждого изменения.
/// </summary>
[Collection("postgres")]
public sealed class CategoriesTests(PostgresFixture fixture) : IAsyncLifetime
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
        var code = $"cat_{suffix}";
        var name = $"Категория {suffix}";

        Guid id;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/categories",
            token,
            $$"""{"code":"{{code}}","name":"{{name}}","icon":"drop","sortOrder":7,"isActive":true}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);

            var category = await CatalogApi.ReadJsonAsync(response, ct);
            id = category.GetProperty("id").GetGuid();
            category.GetProperty("code").GetString().ShouldBe(code);
            category.GetProperty("name").GetString().ShouldBe(name);
            category.GetProperty("icon").GetString().ShouldBe("drop");
            category.GetProperty("sortOrder").GetInt32().ShouldBe(7);
            category.GetProperty("isActive").GetBoolean().ShouldBeTrue();
        }

        // Чтение публичное: аноним видит созданную категорию в справочнике.
        using (var response = await _api.GetAsync("/api/v1/catalog/categories", accessToken: null, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var categories = await CatalogApi.ReadJsonAsync(response, ct);
            var created = categories.EnumerateArray()
                .Single(category => category.GetProperty("id").GetGuid() == id);
            created.GetProperty("code").GetString().ShouldBe(code);
        }

        var updatedName = $"{name} обновлена";
        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"/api/v1/catalog/categories/{id}",
            token,
            $$"""{"code":"{{code}}","name":"{{updatedName}}","icon":null,"sortOrder":3,"isActive":false}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var category = await CatalogApi.ReadJsonAsync(response, ct);
            category.GetProperty("name").GetString().ShouldBe(updatedName);
            category.GetProperty("icon").ValueKind.ShouldBe(JsonValueKind.Null);
            category.GetProperty("sortOrder").GetInt32().ShouldBe(3);
            category.GetProperty("isActive").GetBoolean().ShouldBeFalse();
        }

        var createEntry = await _api.FindAuditAsync("catalog.category.create", id.ToString(), ct);
        createEntry.ShouldNotBeNull("Создание категории должно попасть в аудит.");
        createEntry!.ActorId.ShouldBe(CatalogApi.ModeratorId);
        createEntry.EntityType.ShouldBe("category");
        createEntry.Before.ShouldBeNull();
        createEntry.After.ShouldNotBeNull();
        createEntry.After!.Value.GetProperty("code").GetString().ShouldBe(code);

        var updateEntry = await _api.FindAuditAsync("catalog.category.update", id.ToString(), ct);
        updateEntry.ShouldNotBeNull("Изменение категории должно попасть в аудит.");
        updateEntry!.Before.ShouldNotBeNull();
        updateEntry.Before!.Value.GetProperty("name").GetString().ShouldBe(name);
        updateEntry.After.ShouldNotBeNull();
        updateEntry.After!.Value.GetProperty("name").GetString().ShouldBe(updatedName);

        using (var response = await _api.SendAsync(
            HttpMethod.Delete,
            $"/api/v1/catalog/categories/{id}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var deleteEntry = await _api.FindAuditAsync("catalog.category.delete", id.ToString(), ct);
        deleteEntry.ShouldNotBeNull("Удаление категории должно попасть в аудит.");
        deleteEntry!.Before.ShouldNotBeNull();
        deleteEntry.Before!.Value.GetProperty("name").GetString().ShouldBe(updatedName);
        deleteEntry.After.ShouldBeNull();

        // После удаления записи в справочнике нет.
        using (var response = await _api.GetAsync("/api/v1/catalog/categories", accessToken: null, ct))
        {
            var categories = await CatalogApi.ReadJsonAsync(response, ct);
            categories.EnumerateArray()
                .Any(category => category.GetProperty("id").GetGuid() == id)
                .ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Anonymous_can_read_but_cannot_write()
    {
        var ct = CancellationToken.None;
        var suffix = CatalogApi.NewSuffix();

        using (var response = await _api.GetAsync("/api/v1/catalog/categories", accessToken: null, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/categories",
            accessToken: null,
            $$"""{"code":"cat_{{suffix}}","name":"Категория"}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        }

        (await _api.CountAsync(
            $"select count(*) from categories where code = 'cat_{suffix}'",
            ct)).ShouldBe(0L);
    }

    [Fact]
    public async Task User_forbidden_moderator_has_permission()
    {
        var ct = CancellationToken.None;
        var userToken = await _api.UserTokenAsync(ct);
        var moderatorToken = await _api.ModeratorTokenAsync(ct);
        var suffix = CatalogApi.NewSuffix();
        var code = $"cat_{suffix}";

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/categories",
            userToken,
            $$"""{"code":"{{code}}","name":"Категория"}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/categories",
            moderatorToken,
            $$"""{"code":"{{code}}","name":"Категория"}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        (await _api.CountAsync($"select count(*) from categories where code = '{code}'", ct))
            .ShouldBe(1L);
    }

    [Fact]
    public async Task Duplicate_code_409()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var code = $"cat_{CatalogApi.NewSuffix()}";

        var id = await _api.CreateCategoryAsync(token, code, "Первая категория", ct);
        id.ShouldNotBe(Guid.Empty);

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/categories",
            token,
            $$"""{"code":"{{code}}","name":"Вторая категория"}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        }

        (await _api.CountAsync($"select count(*) from categories where code = '{code}'", ct))
            .ShouldBe(1L);
    }

    [Fact]
    public async Task Delete_category_in_use_409()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var suffix = CatalogApi.NewSuffix();
        var categoryId = await _api.CreateCategoryAsync(
            token,
            $"cat_{suffix}",
            $"Категория {suffix}",
            ct);

        // Тип объекта и группа ссылаются на категорию — удалять нельзя.
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/object-types",
            token,
            $$"""{"categoryId":"{{categoryId}}","code":"type_{{suffix}}","name":"Тип","attributesSchema":{} }""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/groups",
            token,
            $$"""{"categoryId":"{{categoryId}}","name":"Группа {{suffix}}"}""",
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
            response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        }

        // Убираем ссылки — категория удаляется.
        await _api.ExecuteAsync($"delete from object_types where category_id = '{categoryId}'", ct);
        await _api.ExecuteAsync($"delete from object_groups where category_id = '{categoryId}'", ct);

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
    public async Task Unknown_id_update_and_delete_404()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var unknownId = Guid.NewGuid();

        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"/api/v1/catalog/categories/{unknownId}",
            token,
            """{"code":"cat_unknown","name":"Категория"}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Delete,
            $"/api/v1/catalog/categories/{unknownId}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }

}
