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
/// Источники и политики: CRUD,
/// проверки формата чата и таймаута, вкл/выкл, аудит, права, каскадное удаление и контракт
/// <see cref="ISourceDirectory"/> для других модулей. Хост Api поднимается на общей БД
/// коллекции; изоляция — уникальные названия и идентификаторы чатов на каждый тест.
/// </summary>
[Collection("postgres")]
public sealed class SourcesTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string SourcesPath = "/api/v1/sources";
    private const string ObjectsPath = "/api/v1/catalog/objects";

    private CatalogApi _api = null!;

    public async Task InitializeAsync()
    {
        _api = new CatalogApi(fixture);
        await _api.InitializeAsync();
    }

    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task Create_read_update()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var name = $"Чат жильцов {CatalogApi.NewSuffix()}";
        var chatId = $"@water_{CatalogApi.NewSuffix()}";

        Guid sourceId;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            SourcesPath,
            token,
            SourceJson(name, chatId: chatId, timeoutSeconds: 7200, userPrompt: "Ищи упоминания воды"),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);

            var json = await CatalogApi.ReadJsonAsync(response, ct);
            sourceId = json.GetProperty("id").GetGuid();
            json.GetProperty("name").GetString().ShouldBe(name);
            json.GetProperty("sourceType").GetString().ShouldBe("telegram_chat");
            json.GetProperty("chatId").GetString().ShouldBe(chatId);
            json.GetProperty("timeoutSeconds").GetInt32().ShouldBe(7200);
            json.GetProperty("confidenceThreshold").ValueKind.ShouldBe(JsonValueKind.Null);
            json.GetProperty("prefilter").ValueKind.ShouldBe(JsonValueKind.Null);
            json.GetProperty("userPrompt").GetString().ShouldBe("Ищи упоминания воды");
            json.GetProperty("linkByText").GetBoolean().ShouldBeTrue();
            json.GetProperty("isEnabled").GetBoolean().ShouldBeTrue();
            json.GetProperty("state").GetString().ShouldBe("unknown");
            json.GetProperty("statuses").GetArrayLength().ShouldBe(0);
            json.GetProperty("createdAt").ValueKind.ShouldBe(JsonValueKind.String);
        }

        using (var response = await _api.GetAsync($"{SourcesPath}/{sourceId}", token, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var json = await CatalogApi.ReadJsonAsync(response, ct);
            json.GetProperty("id").GetGuid().ShouldBe(sourceId);
            json.GetProperty("chatId").GetString().ShouldBe(chatId);
        }

        var updatedName = $"Обновлённый чат {CatalogApi.NewSuffix()}";
        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"{SourcesPath}/{sourceId}",
            token,
            SourceJson(updatedName, chatId: chatId, timeoutSeconds: 3600, userPrompt: "Новый промпт"),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var json = await CatalogApi.ReadJsonAsync(response, ct);
            json.GetProperty("name").GetString().ShouldBe(updatedName);
            json.GetProperty("timeoutSeconds").GetInt32().ShouldBe(3600);
            json.GetProperty("userPrompt").GetString().ShouldBe("Новый промпт");
        }

        using (var response = await _api.GetAsync($"{SourcesPath}/{sourceId}", token, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var json = await CatalogApi.ReadJsonAsync(response, ct);
            json.GetProperty("name").GetString().ShouldBe(updatedName);
            json.GetProperty("userPrompt").GetString().ShouldBe("Новый промпт");
        }
    }

    [Fact]
    public async Task Timeout_zero_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            SourcesPath,
            token,
            SourceJson($"Нулевой таймаут {CatalogApi.NewSuffix()}", timeoutSeconds: 0),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        json.GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "timeoutSeconds");
    }

    [Fact]
    public async Task Unknown_type_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            SourcesPath,
            token,
            SourceJson($"Неизвестный тип {CatalogApi.NewSuffix()}", sourceType: "max_sms"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        json.GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "sourceType");
    }

    [Fact]
    public async Task Bad_chat_id_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            SourcesPath,
            token,
            SourceJson($"Плохой чат {CatalogApi.NewSuffix()}", chatId: "не чат!"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        json.GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "chatId");
    }

    [Fact]
    public async Task Disable_enable_changes_isEnabled_and_audited()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var sourceId = await CreateSourceAsync(token, ct);

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{sourceId}/disable",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("isEnabled").GetBoolean().ShouldBeFalse();
        }

        (await _api.CountAsync(
            $"select count(*) from sources where id = '{sourceId}' and is_enabled = false",
            ct)).ShouldBe(1L);

        var disableEntry = await _api.FindAuditAsync("source.disable", sourceId.ToString(), ct);
        disableEntry.ShouldNotBeNull("Выключение источника должно попасть в аудит.");
        disableEntry!.ActorId.ShouldBe(CatalogApi.ModeratorId);
        disableEntry.EntityType.ShouldBe("source");

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{sourceId}/enable",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("isEnabled").GetBoolean().ShouldBeTrue();
        }

        (await _api.CountAsync(
            $"select count(*) from sources where id = '{sourceId}' and is_enabled = true",
            ct)).ShouldBe(1L);

        (await _api.FindAuditAsync("source.enable", sourceId.ToString(), ct))
            .ShouldNotBeNull("Включение источника должно попасть в аудит.");
    }

    [Fact]
    public async Task Prompt_change_audit_has_before_and_after()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var sourceId = await CreateSourceAsync(token, ct, userPrompt: "Старый промпт");

        using var response = await _api.SendAsync(
            HttpMethod.Put,
            $"{SourcesPath}/{sourceId}",
            token,
            SourceJson(
                $"Промпт {CatalogApi.NewSuffix()}",
                chatId: $"@prompt_{CatalogApi.NewSuffix()}",
                timeoutSeconds: 3600,
                userPrompt: "Новый промпт"),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var entry = await _api.FindAuditAsync("source.update", sourceId.ToString(), ct);
        entry.ShouldNotBeNull("Изменение промпта должно попасть в аудит.");
        entry!.Before.ShouldNotBeNull();
        entry.After.ShouldNotBeNull();
        entry.Before!.Value.GetProperty("userPrompt").GetString().ShouldBe("Старый промпт");
        entry.After!.Value.GetProperty("userPrompt").GetString().ShouldBe("Новый промпт");
    }

    [Fact]
    public async Task Delete_without_observations_204()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var sourceId = await CreateSourceAsync(token, ct);

        using (var response = await _api.SendAsync(
            HttpMethod.Delete,
            $"{SourcesPath}/{sourceId}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await _api.CountAsync($"select count(*) from sources where id = '{sourceId}'", ct))
            .ShouldBe(0L);
        (await _api.CountAsync(
            $"select count(*) from source_policies where source_id = '{sourceId}'",
            ct)).ShouldBe(0L);

        var deleteEntry = await _api.FindAuditAsync("source.delete", sourceId.ToString(), ct);
        deleteEntry.ShouldNotBeNull("Удаление источника должно попасть в аудит.");
        deleteEntry!.Before.ShouldNotBeNull();
        deleteEntry.After.ShouldBeNull();
    }

    [Fact]
    public async Task Delete_with_observations_409()
    {
        var ct = CancellationToken.None;
        using var factory = new TestApiFactory(fixture.ConnectionString);

        using var overridden = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISourceDeletionGuard>();
            services.AddScoped<ISourceDeletionGuard>(_ => new AlwaysHasObservationsGuard());
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

        Guid sourceId;
        using (var response = await SendAsync(
            client,
            HttpMethod.Post,
            SourcesPath,
            token,
            SourceJson($"С наблюдениями {CatalogApi.NewSuffix()}"),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            sourceId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
        }

        using (var response = await SendAsync(
            client,
            HttpMethod.Delete,
            $"{SourcesPath}/{sourceId}",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

            var json = await CatalogApi.ReadJsonAsync(response, ct);
            json.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("disable");
        }

        (await _api.CountAsync($"select count(*) from sources where id = '{sourceId}'", ct))
            .ShouldBe(1L);
    }

    [Fact]
    public async Task User_403_moderator_200_anonymous_401()
    {
        var ct = CancellationToken.None;
        var userToken = await _api.UserTokenAsync(ct);
        var moderatorToken = await _api.ModeratorTokenAsync(ct);

        using (var response = await _api.GetAsync(SourcesPath, accessToken: null, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var response = await _api.GetAsync(SourcesPath, userToken, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var response = await _api.GetAsync(SourcesPath, moderatorToken, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            SourcesPath,
            userToken,
            SourceJson($"Права {CatalogApi.NewSuffix()}"),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task LinkByText_defaults_to_true()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var suffix = CatalogApi.NewSuffix();

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            SourcesPath,
            token,
            $$"""
              {"name":"Без флага {{suffix}}","sourceType":"telegram_chat",
               "chatId":"@link_{{suffix}}","timeoutSeconds":3600,"userPrompt":""}
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        json.GetProperty("linkByText").GetBoolean().ShouldBeTrue();

        var sourceId = json.GetProperty("id").GetGuid();
        (await _api.CountAsync(
            $"select count(*) from sources where id = '{sourceId}' and link_by_text = true",
            ct)).ShouldBe(1L);
    }

    [Fact]
    public async Task Policy_created_with_source()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var suffix = CatalogApi.NewSuffix();

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            SourcesPath,
            token,
            $$"""
              {"name":"Политика {{suffix}}","sourceType":"telegram_chat",
               "chatId":"@policy_{{suffix}}","timeoutSeconds":7200,
               "confidenceThreshold":0.42,"prefilter":{"keywords":["вода"]},
               "userPrompt":"Ищи воду"}
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var sourceId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();

        (await _api.CountAsync(
            $"""
             select count(*) from source_policies
             where source_id = '{sourceId}'
               and timeout = interval '7200 seconds'
               and confidence_threshold between 0.41 and 0.43
               and prefilter is not null
             """,
            ct)).ShouldBe(1L);
    }

    [Fact]
    public async Task Directory_returns_policy_and_enabled_flag()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var sourceId = await CreateSourceAsync(token, ct, timeoutSeconds: 3600);

        await using var scope = _api.Factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<ISourceDirectory>();

        var policy = await directory.GetPolicyAsync(sourceId, ct);
        policy.ShouldNotBeNull();
        policy!.SourceId.ShouldBe(sourceId);
        policy.Timeout.ShouldBe(TimeSpan.FromHours(1));
        policy.ConfidenceThreshold.ShouldBeNull();
        policy.SourceType.ShouldBe("telegram_chat");
        policy.LinkByText.ShouldBeTrue();

        (await directory.ExistsAsync(sourceId, ct)).ShouldBeTrue();
        (await directory.ExistsAsync(Guid.NewGuid(), ct)).ShouldBeFalse();
        (await directory.IsEnabledAsync(sourceId, ct)).ShouldBeTrue();
        (await directory.GetStatusesAsync(sourceId, ct)).ShouldBeEmpty();
        (await directory.GetPolicyAsync(Guid.NewGuid(), ct)).ShouldBeNull();

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{sourceId}/disable",
            token,
            json: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await directory.IsEnabledAsync(sourceId, ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Catalog_link_to_missing_source_rejected()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);

        Guid objectId;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            ObjectsPath,
            token,
            $$"""
              {"name":"Фиксация {{CatalogApi.NewSuffix()}}","coverageKind":"point",
               "geometry":{"type":"Point","coordinates":[37.80,48.01]} }
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            objectId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
        }

        using var linkResponse = await _api.SendAsync(
            HttpMethod.Put,
            $"{ObjectsPath}/{objectId}/sources/{Guid.NewGuid()}",
            token,
            json: null,
            ct);

        linkResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Source_types_contains_telegram_and_max_chat()
    {
        var ct = CancellationToken.None;

        (await _api.CountAsync(
            """
            select count(*) from source_types
            where (code = 'telegram_chat' and name = 'Telegram-чат')
               or (code = 'max_chat' and name = 'MAX-чат')
            """,
            ct)).ShouldBe(2L);
    }

    [Fact]
    public async Task Telegram_source_is_created_in_bot_mode()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var sourceId = await CreateSourceAsync(token, ct);

        // В первой версии UI умеет только Bot API, поэтому режим фиксируется на сервере.
        (await _api.CountAsync(
            $"""
             select count(*) from sources
             where id = '{sourceId}' and settings->>'mode' = 'bot'
             """,
            ct)).ShouldBe(1L);
    }

    [Fact]
    public async Task Created_source_has_empty_statuses()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var sourceId = await CreateSourceAsync(token, ct);

        using var response = await _api.GetAsync($"{SourcesPath}/{sourceId}", token, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CatalogApi.ReadJsonAsync(response, ct))
            .GetProperty("statuses").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Source_responses_contain_statuses_in_display_order()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var suffix = CatalogApi.NewSuffix();
        var name = $"Источник со статусами {suffix}";
        var chatId = $"@statuses_{suffix}";

        Guid sourceId;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            SourcesPath,
            token,
            SourceJson(name, chatId: chatId),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);

            var json = await CatalogApi.ReadJsonAsync(response, ct);
            sourceId = json.GetProperty("id").GetGuid();
            json.GetProperty("statuses").GetArrayLength().ShouldBe(0);
        }

        // Добавляем в порядке, обратном порядку показа: статусы сортируются по sortOrder.
        var second = await CreateStatusAsync(
            token,
            sourceId,
            $"second_{suffix}",
            $"Воды нет {suffix}",
            "Подача прекращена",
            "none",
            "cross",
            sortOrder: 20,
            ct);
        var first = await CreateStatusAsync(
            token,
            sourceId,
            $"first_{suffix}",
            $"Вода есть {suffix}",
            "Подача идёт",
            "ok",
            "drop",
            sortOrder: 10,
            ct);

        using (var response = await _api.GetAsync($"{SourcesPath}/{sourceId}", token, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            AssertStatuses(await CatalogApi.ReadJsonAsync(response, ct), first, second);
        }

        using (var response = await _api.GetAsync(
            $"{SourcesPath}?limit=100&q={Uri.EscapeDataString(name)}",
            token,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var page = await CatalogApi.ReadJsonAsync(response, ct);
            var item = page.GetProperty("items").EnumerateArray()
                .Single(source => source.GetProperty("id").GetGuid() == sourceId);
            AssertStatuses(item, first, second);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"{SourcesPath}/{sourceId}",
            token,
            SourceJson($"Обновлённый {name}", chatId: chatId),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            AssertStatuses(await CatalogApi.ReadJsonAsync(response, ct), first, second);
        }
    }

    private Task<Guid> CreateSourceAsync(
        string token,
        CancellationToken ct,
        string? userPrompt = null,
        int timeoutSeconds = 3600) =>
        CreateSourceWithPromptAsync(token, userPrompt ?? string.Empty, timeoutSeconds, ct);

    private async Task<Guid> CreateSourceWithPromptAsync(
        string token,
        string userPrompt,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var suffix = CatalogApi.NewSuffix();

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            SourcesPath,
            token,
            SourceJson(
                $"Источник {suffix}",
                chatId: $"@source_{suffix}",
                timeoutSeconds: timeoutSeconds,
                userPrompt: userPrompt),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
    }

    private static string SourceJson(
        string name,
        string sourceType = "telegram_chat",
        string chatId = "@source_chat",
        int timeoutSeconds = 3600,
        string userPrompt = "") =>
        $$"""
          {"name":"{{name}}","sourceType":"{{sourceType}}","chatId":"{{chatId}}",
           "timeoutSeconds":{{timeoutSeconds.ToString(CultureInfo.InvariantCulture)}},
           "userPrompt":"{{userPrompt}}"}
          """;

    private async Task<StatusExpectation> CreateStatusAsync(
        string token,
        Guid sourceId,
        string code,
        string name,
        string description,
        string color,
        string icon,
        int sortOrder,
        CancellationToken ct)
    {
        using var response = await _api.SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{sourceId}/statuses",
            token,
            $$"""
              {"code":"{{code}}","name":"{{name}}","description":"{{description}}",
               "color":"{{color}}","icon":"{{icon}}",
               "sortOrder":{{sortOrder.ToString(CultureInfo.InvariantCulture)}}}
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var json = await CatalogApi.ReadJsonAsync(response, ct);

        return new StatusExpectation(
            json.GetProperty("id").GetGuid(),
            code,
            name,
            description,
            color,
            icon,
            sortOrder);
    }

    /// <summary>Проверяет <c>statuses</c> источника по схеме <c>SourceStatus</c> и порядок показа.</summary>
    private static void AssertStatuses(JsonElement source, params StatusExpectation[] expected)
    {
        var statuses = source.GetProperty("statuses").EnumerateArray().ToArray();

        statuses.Length.ShouldBe(expected.Length);

        for (var index = 0; index < expected.Length; index++)
        {
            var status = statuses[index];

            status.GetProperty("id").GetGuid().ShouldBe(expected[index].Id);
            status.GetProperty("code").GetString().ShouldBe(expected[index].Code);
            status.GetProperty("name").GetString().ShouldBe(expected[index].Name);
            status.GetProperty("description").GetString().ShouldBe(expected[index].Description);
            status.GetProperty("color").GetString().ShouldBe(expected[index].Color);
            status.GetProperty("icon").GetString().ShouldBe(expected[index].Icon);
            status.GetProperty("sortOrder").GetInt32().ShouldBe(expected[index].SortOrder);
            status.GetProperty("statusDefinitionId").ValueKind.ShouldBe(JsonValueKind.Null);
        }
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

    /// <summary>Ожидаемый элемент ответа <c>SourceStatus</c> для проверки контракта.</summary>
    private sealed record StatusExpectation(
        Guid Id,
        string Code,
        string Name,
        string Description,
        string Color,
        string Icon,
        int SortOrder);

    /// <summary>
    /// Guard с наблюдениями: реакция endpoint на «у источника есть наблюдения» проверяется
    /// подменой реализации <see cref="ISourceDeletionGuard"/> в DI, без данных в БД. Реальные
    /// реализации и сквозной 409 с наблюдениями — в
    /// <c>Observations/ObservationStorageTests</c>.
    /// </summary>
    private sealed class AlwaysHasObservationsGuard : ISourceDeletionGuard
    {
        public Task<bool> HasObservationsAsync(Guid sourceId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task Site_source_cannot_be_created_directly_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var suffix = CatalogApi.NewSuffix();

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            SourcesPath,
            token,
            $$"""
              {"name":"Сайт {{suffix}}","sourceType":"site","timeoutSeconds":10800,
               "statuses":[{"code":"ok","name":"Вода есть","color":"ok","icon":"drop"}]}
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "sourceType");
    }

    [Fact]
    public async Task Site_source_cannot_be_deleted_409()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var categoryId = await _api.CreateCategoryAsync(token, ct: ct);
        var siteId = await ReadSiteSourceIdAsync(categoryId, ct);

        using var response = await _api.SendAsync(
            HttpMethod.Delete,
            $"{SourcesPath}/{siteId}",
            token,
            json: null,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("detail").GetString()
            .ShouldNotBeNull().ShouldContain("категори");
    }

    [Fact]
    public async Task Site_source_cannot_be_linked_to_object_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var categoryId = await _api.CreateCategoryAsync(token, ct: ct);
        var siteId = await ReadSiteSourceIdAsync(categoryId, ct);
        var suffix = CatalogApi.NewSuffix();

        Guid objectId;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            ObjectsPath,
            token,
            $$"""
              {"name":"Фиксация {{suffix}}","coverageKind":"point",
               "geometry":{"type":"Point","coordinates":[37.80,48.01]},"sourceIds":[]}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            objectId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
        }

        using var linked = await _api.SendAsync(
            HttpMethod.Put,
            $"{ObjectsPath}/{objectId}/sources/{siteId}",
            token,
            json: null,
            ct);

        linked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CatalogApi.ReadJsonAsync(linked, ct)).GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "sourceId");
    }

    [Fact]
    public async Task Site_source_can_update_name_and_timeout_only()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var categoryId = await _api.CreateCategoryAsync(token, ct: ct);
        var siteId = await ReadSiteSourceIdAsync(categoryId, ct);
        var name = $"Сайт обновлён {CatalogApi.NewSuffix()}";

        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"{SourcesPath}/{siteId}",
            token,
            $$"""{"name":"{{name}}","sourceType":"site","timeoutSeconds":7200}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var json = await CatalogApi.ReadJsonAsync(response, ct);
            json.GetProperty("name").GetString().ShouldBe(name);
            json.GetProperty("timeoutSeconds").GetInt32().ShouldBe(7200);
            json.GetProperty("sourceType").GetString().ShouldBe("site");
            json.GetProperty("categoryId").GetGuid().ShouldBe(categoryId);
            json.GetProperty("chatId").GetString().ShouldBeEmpty();
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"{SourcesPath}/{siteId}",
            token,
            $$"""
              {"name":"{{name}}","sourceType":"site","timeoutSeconds":7200,
               "chatId":"@site_chat","userPrompt":"промпт","linkByText":true}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("errors").EnumerateArray()
                .Select(error => error.GetProperty("field").GetString())
                .ShouldContain("chatId");
        }
    }

    private async Task<Guid> ReadSiteSourceIdAsync(Guid categoryId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select id from sources
            where category_id = @category and source_type = 'site'
            """,
            connection);
        command.Parameters.AddWithValue("category", NpgsqlTypes.NpgsqlDbType.Uuid, categoryId);

        return (Guid)(await command.ExecuteScalarAsync(ct))!;
    }
}
