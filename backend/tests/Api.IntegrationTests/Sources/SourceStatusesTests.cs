using System.Globalization;
using System.Net;
using System.Text.Json;

using Api.IntegrationTests.Catalog;
using Api.IntegrationTests.Identity;
using Api.IntegrationTests.Infrastructure;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using Shouldly;

using Sources.Contracts;

namespace Api.IntegrationTests.Sources;

/// <summary>
/// Статусы источника: создание из справочника и
/// собственного статуса, независимые списки источников, палитра цветов, 409 на дубль
/// и на удаление статуса с наблюдениями, порядок выдачи, права, аудит и проверка БД.
/// Хост Api поднимается на общей БД коллекции; изоляция — суффикс в коде/названии на каждый тест.
/// </summary>
[Collection("postgres")]
public sealed class SourceStatusesTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string SourcesPath = "/api/v1/sources";
    private const string CatalogStatusesPath = "/api/v1/catalog/statuses";

    private CatalogApi _api = null!;

    public async Task InitializeAsync()
    {
        _api = new CatalogApi(fixture);
        await _api.InitializeAsync();
    }

    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task Create_from_definition_copies_code_name_color_icon_and_audited()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var sourceId = await _api.CreateSourceAsync(token, ct: ct);
        var suffix = CatalogApi.NewSuffix();

        var definitionId = await CreateDefinitionAsync(token, suffix, "limited", "alert-triangle", ct);

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{sourceId}/statuses",
            token,
            $$"""
              {"statusDefinitionId":"{{definitionId}}","description":"Подсказка для LLM","sortOrder":7}
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var status = await CatalogApi.ReadJsonAsync(response, ct);
        var statusId = status.GetProperty("id").GetGuid();
        status.GetProperty("code").GetString().ShouldBe($"def_{suffix}");
        status.GetProperty("name").GetString().ShouldBe($"Справочный статус {suffix}");
        status.GetProperty("description").GetString().ShouldBe("Подсказка для LLM");
        status.GetProperty("color").GetString().ShouldBe("limited");
        status.GetProperty("icon").GetString().ShouldBe("alert-triangle");
        status.GetProperty("sortOrder").GetInt32().ShouldBe(7);
        status.GetProperty("statusDefinitionId").GetGuid().ShouldBe(definitionId);

        var entry = await _api.FindAuditAsync("source.status.create", statusId.ToString(), ct);
        entry.ShouldNotBeNull("Создание статуса должно попасть в аудит.");
        entry!.ActorId.ShouldBe(CatalogApi.ModeratorId);
        entry.EntityType.ShouldBe("source_status");
        entry.Before.ShouldBeNull();
        entry.After!.Value.GetProperty("color").GetString().ShouldBe("limited");
    }

    [Fact]
    public async Task Custom_status_create_read_update_delete_and_audit()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var sourceId = await _api.CreateSourceAsync(token, ct: ct);
        var suffix = CatalogApi.NewSuffix();
        var code = $"water_{suffix}";

        Guid statusId;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{sourceId}/statuses",
            token,
            StatusJson(code, $"Вода {suffix}", description: "Подача идёт", color: "ok", icon: "drop", sortOrder: 3),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);

            var status = await CatalogApi.ReadJsonAsync(response, ct);
            statusId = status.GetProperty("id").GetGuid();
            status.GetProperty("code").GetString().ShouldBe(code);
            status.GetProperty("name").GetString().ShouldBe($"Вода {suffix}");
            status.GetProperty("description").GetString().ShouldBe("Подача идёт");
            status.GetProperty("color").GetString().ShouldBe("ok");
            status.GetProperty("icon").GetString().ShouldBe("drop");
            status.GetProperty("sortOrder").GetInt32().ShouldBe(3);
            status.GetProperty("statusDefinitionId").ValueKind.ShouldBe(JsonValueKind.Null);
        }

        using (var response = await _api.GetAsync($"{SourcesPath}/{sourceId}/statuses", token, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var statuses = await CatalogApi.ReadJsonAsync(response, ct);
            statuses.EnumerateArray()
                .Single(item => item.GetProperty("id").GetGuid() == statusId)
                .GetProperty("code").GetString()
                .ShouldBe(code);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"{SourcesPath}/{sourceId}/statuses/{statusId}",
            token,
            StatusJson(code, $"Вода обновлена {suffix}", description: "Подача возобновилась", color: "limited", icon: "alert", sortOrder: 1),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var status = await CatalogApi.ReadJsonAsync(response, ct);
            status.GetProperty("name").GetString().ShouldBe($"Вода обновлена {suffix}");
            status.GetProperty("description").GetString().ShouldBe("Подача возобновилась");
            status.GetProperty("color").GetString().ShouldBe("limited");
            status.GetProperty("sortOrder").GetInt32().ShouldBe(1);
        }

        var updateEntry = await _api.FindAuditAsync("source.status.update", statusId.ToString(), ct);
        updateEntry.ShouldNotBeNull("Изменение статуса должно попасть в аудит.");
        updateEntry!.Before!.Value.GetProperty("color").GetString().ShouldBe("ok");
        updateEntry.After!.Value.GetProperty("color").GetString().ShouldBe("limited");

        using (var response = await _api.SendAsync(
            HttpMethod.Delete,
            $"{SourcesPath}/{sourceId}/statuses/{statusId}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await _api.CountAsync(
            $"select count(*) from source_statuses where id = '{statusId}'",
            ct)).ShouldBe(0L);

        var deleteEntry = await _api.FindAuditAsync("source.status.delete", statusId.ToString(), ct);
        deleteEntry.ShouldNotBeNull("Удаление статуса должно попасть в аудит.");
        deleteEntry!.After.ShouldBeNull();
    }

    [Fact]
    public async Task Duplicate_code_in_one_source_409()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var sourceId = await _api.CreateSourceAsync(token, ct: ct);
        var suffix = CatalogApi.NewSuffix();
        var code = $"dup_{suffix}";

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{sourceId}/statuses",
            token,
            StatusJson(code, $"Первый {suffix}"),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{sourceId}/statuses",
            token,
            StatusJson(code, $"Второй {suffix}"),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

            var problem = await CatalogApi.ReadJsonAsync(response, ct);
            problem.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain(code);
        }

        (await _api.CountAsync(
            $"select count(*) from source_statuses where source_id = '{sourceId}' and code = '{code}'",
            ct)).ShouldBe(1L);
    }

    [Fact]
    public async Task Same_code_allowed_for_different_sources()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var firstSourceId = await _api.CreateSourceAsync(token, ct: ct);
        var secondSourceId = await _api.CreateSourceAsync(token, ct: ct);
        var suffix = CatalogApi.NewSuffix();
        var code = $"shared_{suffix}";

        foreach (var sourceId in new[] { firstSourceId, secondSourceId })
        {
            using var response = await _api.SendAsync(
                HttpMethod.Post,
                $"{SourcesPath}/{sourceId}/statuses",
                token,
                StatusJson(code, $"Вода {suffix}"),
                ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        foreach (var sourceId in new[] { firstSourceId, secondSourceId })
        {
            using var response = await _api.GetAsync($"{SourcesPath}/{sourceId}/statuses", token, ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await CatalogApi.ReadJsonAsync(response, ct)).GetArrayLength().ShouldBe(1);
        }
    }

    [Fact]
    public async Task Color_outside_palette_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var sourceId = await _api.CreateSourceAsync(token, ct: ct);
        var suffix = CatalogApi.NewSuffix();

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{sourceId}/statuses",
            token,
            StatusJson($"color_{suffix}", $"Цвет {suffix}", color: "red"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var problem = await CatalogApi.ReadJsonAsync(response, ct);
        problem.GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "color");
    }

    [Fact]
    public async Task Unknown_source_and_foreign_status_404()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var suffix = CatalogApi.NewSuffix();
        var missingSourceId = Guid.NewGuid();

        using (var response = await _api.GetAsync(
            $"{SourcesPath}/{missingSourceId}/statuses",
            token,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{missingSourceId}/statuses",
            token,
            StatusJson($"missing_{suffix}", $"Статус {suffix}"),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        var sourceId = await _api.CreateSourceAsync(token, ct: ct);
        var otherSourceId = await _api.CreateSourceAsync(token, ct: ct);
        var code = $"foreign_{suffix}";

        Guid statusId;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{sourceId}/statuses",
            token,
            StatusJson(code, $"Статус {suffix}"),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            statusId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"{SourcesPath}/{otherSourceId}/statuses/{statusId}",
            token,
            StatusJson(code, $"Чужой {suffix}"),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Delete,
            $"{SourcesPath}/{otherSourceId}/statuses/{statusId}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        (await _api.CountAsync(
            $"select count(*) from source_statuses where id = '{statusId}'",
            ct)).ShouldBe(1L);
    }

    [Fact]
    public async Task Unknown_status_definition_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var sourceId = await _api.CreateSourceAsync(token, ct: ct);
        var suffix = CatalogApi.NewSuffix();

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{sourceId}/statuses",
            token,
            $$"""{"statusDefinitionId":"{{Guid.NewGuid()}}"}""",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var problem = await CatalogApi.ReadJsonAsync(response, ct);
        problem.GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "statusDefinitionId");
    }

    [Fact]
    public async Task Sort_order_orders_list_and_directory()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var sourceId = await _api.CreateSourceAsync(token, ct: ct);
        var suffix = CatalogApi.NewSuffix();
        var codes = new[] { $"third_{suffix}", $"first_{suffix}", $"second_{suffix}" };
        var sortOrders = new[] { 30, 10, 20 };

        for (var index = 0; index < codes.Length; index++)
        {
            using var response = await _api.SendAsync(
                HttpMethod.Post,
                $"{SourcesPath}/{sourceId}/statuses",
                token,
                StatusJson(codes[index], $"Статус {index} {suffix}", sortOrder: sortOrders[index]),
                ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using (var response = await _api.GetAsync($"{SourcesPath}/{sourceId}/statuses", token, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var statuses = await CatalogApi.ReadJsonAsync(response, ct);
            statuses.EnumerateArray()
                .Select(item => item.GetProperty("code").GetString())
                .ShouldBe([codes[1], codes[2], codes[0]]);
        }

        await using var scope = _api.Factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<ISourceDirectory>();

        var fromDirectory = await directory.GetStatusesAsync(sourceId, ct);
        fromDirectory.Select(status => status.Code).ShouldBe([codes[1], codes[2], codes[0]]);
        fromDirectory[0].Name.ShouldBe($"Статус 1 {suffix}");
        fromDirectory[0].Color.ShouldBe("ok");
        fromDirectory[0].Icon.ShouldBe("drop");
    }

    [Fact]
    public async Task Delete_with_observations_409()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var sourceId = await _api.CreateSourceAsync(token, ct: ct);
        var suffix = CatalogApi.NewSuffix();

        Guid statusId;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{sourceId}/statuses",
            token,
            StatusJson($"guarded_{suffix}", $"Статус {suffix}"),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            statusId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
        }

        using var factory = new TestApiFactory(fixture.ConnectionString);
        using var overridden = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISourceStatusDeletionGuard>();
            services.AddScoped<ISourceStatusDeletionGuard>(_ => new AlwaysHasObservationsGuard());
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

        using (var response = await SendAsync(
            client,
            HttpMethod.Delete,
            $"{SourcesPath}/{sourceId}/statuses/{statusId}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

            var problem = await CatalogApi.ReadJsonAsync(response, ct);
            problem.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("наблюдения");
        }

        (await _api.CountAsync(
            $"select count(*) from source_statuses where id = '{statusId}'",
            ct)).ShouldBe(1L);
    }

    [Fact]
    public async Task User_403_and_anonymous_401()
    {
        var ct = CancellationToken.None;
        var userToken = await _api.UserTokenAsync(ct);
        var moderatorToken = await _api.ModeratorTokenAsync(ct);
        var sourceId = await _api.CreateSourceAsync(moderatorToken, ct: ct);
        var suffix = CatalogApi.NewSuffix();

        using (var response = await _api.GetAsync($"{SourcesPath}/{sourceId}/statuses", accessToken: null, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var response = await _api.GetAsync($"{SourcesPath}/{sourceId}/statuses", userToken, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{sourceId}/statuses",
            userToken,
            StatusJson($"forbidden_{suffix}", $"Статус {suffix}"),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var response = await _api.GetAsync($"{SourcesPath}/{sourceId}/statuses", moderatorToken, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    private async Task<Guid> CreateDefinitionAsync(
        string token,
        string suffix,
        string color,
        string icon,
        CancellationToken ct)
    {
        using var response = await _api.SendAsync(
            HttpMethod.Post,
            CatalogStatusesPath,
            token,
            $$"""
              {"code":"def_{{suffix}}","name":"Справочный статус {{suffix}}",
               "color":"{{color}}","icon":"{{icon}}"}
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
    }

    private static string StatusJson(
        string code,
        string name,
        string description = "",
        string color = "ok",
        string icon = "drop",
        int sortOrder = 0) =>
        $$"""
          {"code":"{{code}}","name":"{{name}}","description":"{{description}}",
           "color":"{{color}}","icon":"{{icon}}",
           "sortOrder":{{sortOrder.ToString(CultureInfo.InvariantCulture)}}}
          """;

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
    /// Guard с наблюдениями: реакция endpoint на «у статуса есть наблюдения» проверяется
    /// подменой реализации <see cref="ISourceStatusDeletionGuard"/> в DI, без данных в БД.
    /// Реальные реализации и сквозной 409 с наблюдениями — в
    /// <c>Observations/ObservationStorageTests</c>.
    /// </summary>
    private sealed class AlwaysHasObservationsGuard : ISourceStatusDeletionGuard
    {
        public Task<bool> HasObservationsAsync(Guid sourceId, Guid sourceStatusId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            return Task.FromResult(true);
        }
    }
}
