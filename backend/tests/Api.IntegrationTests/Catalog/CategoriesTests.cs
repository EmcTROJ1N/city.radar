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
            $$"""{"categoryId":"{{categoryId}}","code":"type_{{suffix}}","name":"Тип"}""",
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

    [Fact]
    public async Task Creating_category_creates_site_source_with_category_statuses()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var suffix = CatalogApi.NewSuffix();
        var code = $"cat_site_{suffix}";
        var name = $"Категория сайта {suffix}";

        var categoryId = await _api.CreateCategoryAsync(token, code, name, ct);

        (await _api.CountAsync(
            $"""
             select count(*) from sources
             where category_id = '{categoryId}' and source_type = 'site' and name = 'Сайт · {name}'
             """,
            ct)).ShouldBe(1L);

        (await _api.CountAsync(
            $"""
             select count(*) from source_policies p
             join sources s on s.id = p.source_id
             where s.category_id = '{categoryId}'
               and s.source_type = 'site'
               and p.timeout = interval '3 hours'
             """,
            ct)).ShouldBe(1L);

        // Статусы справочника появляются уже после создания категории; повторное сохранение
        // категории синхронизирует их в «Сайт».
        await _api.ExecuteAsync(
            $"""
             insert into status_definitions
               (category_id, code, name, color, icon, severity, is_positive)
             values
               ('{categoryId}', 'site_yes_{suffix}', 'Вода есть', 'ok', 'drop', 0, true),
               ('{categoryId}', 'site_no_{suffix}', 'Воды нет', 'none', 'cross', 3, false)
             """,
            ct);

        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"/api/v1/catalog/categories/{categoryId}",
            token,
            $$"""{"code":"{{code}}","name":"{{name}}"}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await _api.CountAsync(
            $"""
             select count(*) from source_statuses ss
             join sources s on s.id = ss.source_id
             where s.category_id = '{categoryId}'
               and s.source_type = 'site'
               and ss.code in ('site_yes_{suffix}', 'site_no_{suffix}')
             """,
            ct)).ShouldBe(2L);
    }

    [Fact]
    public async Task Deleting_category_without_observations_removes_site_source()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var categoryId = await _api.CreateCategoryAsync(token, ct: ct);

        (await _api.CountAsync(
            $"select count(*) from sources where category_id = '{categoryId}' and source_type = 'site'",
            ct)).ShouldBe(1L);

        using (var response = await _api.SendAsync(
            HttpMethod.Delete,
            $"/api/v1/catalog/categories/{categoryId}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await _api.CountAsync(
            $"select count(*) from sources where category_id = '{categoryId}'",
            ct)).ShouldBe(0L);
    }

    [Fact]
    public async Task Deleting_category_with_site_observations_409()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var suffix = CatalogApi.NewSuffix();
        var code = $"cat_obs_{suffix}";
        var categoryId = await _api.CreateCategoryAsync(token, code, $"Категория {suffix}", ct);

        Guid objectTypeId;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/object-types",
            token,
            $$"""
              {"categoryId":"{{categoryId}}","code":"obs_type_{{suffix}}",
               "name":"Тип {{suffix}}","geometryKind":"point"}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            objectTypeId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
        }

        Guid objectId;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/objects",
            token,
            $$"""
              {"name":"Фиксация {{suffix}}","objectTypeId":"{{objectTypeId}}","coverageKind":"point",
               "geometry":{"type":"Point","coordinates":[37.80,48.01]},"sourceIds":[]}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            objectId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
        }

        await _api.ExecuteAsync(
            $"""
             insert into source_statuses (source_id, code, name, color, icon, sort_order)
             select id, 'site_obs_{suffix}', 'Вода есть', 'ok', 'drop', 1
             from sources
             where category_id = '{categoryId}' and source_type = 'site';

             insert into observations
               (map_object_id, source_id, source_status_id, source_type, reporter_id,
                observed_at, received_at, expires_at)
             select '{objectId}', s.id, ss.id, 'manual', '{CatalogApi.ModeratorId}',
                    now(), now(), now() + interval '1 hour'
             from sources s
             join source_statuses ss on ss.source_id = s.id
             where s.category_id = '{categoryId}' and s.source_type = 'site';
             """,
            ct);

        using (var response = await _api.SendAsync(
            HttpMethod.Delete,
            $"/api/v1/catalog/categories/{categoryId}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        (await _api.CountAsync(
            $"select count(*) from sources where category_id = '{categoryId}' and source_type = 'site'",
            ct)).ShouldBe(1L);
    }
}
