using System.Globalization;
using System.Net;
using System.Text.Json;

using Api.IntegrationTests.Catalog;
using Api.IntegrationTests.Identity;
using Api.IntegrationTests.Infrastructure;

using BuildingBlocks.Messaging;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;
using NpgsqlTypes;

using Shouldly;

namespace Api.IntegrationTests.Observations;

/// <summary>
/// Создание ручной отметки и публичный список (010, 013, 014,
///, Review Focus 1). Хост Api поднимается на общей БД коллекции; изоляция
/// новые источники, статусы и фиксации на каждый тест.
/// </summary>
[Collection("postgres")]
public sealed class CreateObservationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string ObservationsPath = "/api/v1/observations";
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
    public async Task User_observation_updates_current_status_within_one_transaction()
    {
        var ct = CancellationToken.None;
        var token = await _api.UserTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);

        var before = DateTimeOffset.UtcNow;

        using var response = await PostObservationAsync(token, setup, ct: ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var json = await CatalogApi.ReadJsonAsync(response, ct);
        var observationId = json.GetProperty("observation").GetProperty("id").GetGuid();

        json.GetProperty("observation").GetProperty("status").GetProperty("id").GetGuid()
            .ShouldBe(setup.StatusA);
        json.GetProperty("status").GetProperty("sourceStatusId").GetGuid().ShouldBe(setup.StatusA);
        json.GetProperty("status").GetProperty("observationCount").GetInt32().ShouldBe(1);

        //: проекция обновлена тем же запросом, что и наблюдение.
        (await _api.CountAsync(
            $"""
             select count(*) from current_statuses
             where map_object_id = '{setup.ObjectId}'
               and source_status_id = '{setup.StatusA}'
               and observation_id = '{observationId}'
             """,
            ct)).ShouldBe(1L);

        (await _api.CountAsync(
            $"""
             select count(*) from observations
             where id = '{observationId}'
               and source_type = 'manual'
               and reporter_id = '{CatalogApi.UserId}'
             """,
            ct)).ShouldBe(1L);

        var read = await ReadObservationRowAsync(observationId, ct);
        read.ShouldNotBeNull();
        read!.ObservedAt.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(-5));
        read.ObservedAt.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(5));

        //: сбой на outbox откатывает наблюдение, проекцию и событие.
        var failingSetup = await CreateSetupAsync(ct);
        using var factory = new TestApiFactory(fixture.ConnectionString);
        using var failing = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IOutbox>();
            services.AddScoped<IOutbox, ThrowingOutbox>();
        }));

        using var client = failing.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });

        using (var health = await client.GetAsync("/health", ct))
        {
            health.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var failingToken = await TestAuth.CreateAccessTokenAsync(failing.Services, CatalogApi.UserId, ct);

        using (var responseWithFailure = await SendAsync(
            client,
            HttpMethod.Post,
            ObservationsPath,
            failingToken,
            ObservationJson(failingSetup),
            ct))
        {
            responseWithFailure.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        }

        (await _api.CountAsync(
            $"select count(*) from observations where map_object_id = '{failingSetup.ObjectId}'",
            ct)).ShouldBe(0L);
        (await _api.CountAsync(
            $"select count(*) from current_statuses where map_object_id = '{failingSetup.ObjectId}'",
            ct)).ShouldBe(0L);
        (await CountOutboxForObjectAsync(failingSetup.ObjectId, ct)).ShouldBe(0L);
    }

    [Fact]
    public async Task Observation_with_source_not_linked_to_object_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);
        var other = await CreateSourceWithStatusesAsync(token, ct);

        using var response = await PostObservationAsync(
            token,
            setup,
            sourceId: other.SourceId,
            sourceStatusId: other.StatusA,
            ct: ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "sourceId");
        (await CountObservationsAsync(setup.ObjectId, ct)).ShouldBe(0L);
    }

    [Fact]
    public async Task Object_without_sources_can_be_marked_via_site_source()
    {
        var ct = CancellationToken.None;
        var moderatorToken = await _api.ModeratorTokenAsync(ct);
        var token = await _api.UserTokenAsync(ct);
        var setup = await CreateSiteSetupAsync(moderatorToken, ct);

        using (var response = await PostObservationAsync(token, setup, ct: ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);

            var json = await CatalogApi.ReadJsonAsync(response, ct);
            json.GetProperty("observation").GetProperty("status").GetProperty("id").GetGuid()
                .ShouldBe(setup.StatusA);
        }

        (await _api.CountAsync(
            $"""
             select count(*) from observations
             where map_object_id = '{setup.ObjectId}'
               and source_id = '{setup.SourceId}'
               and source_type = 'manual'
             """,
            ct)).ShouldBe(1L);
    }

    [Fact]
    public async Task Object_without_type_cannot_be_marked_409()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var source = await CreateSourceWithStatusesAsync(token, ct);
        var objectId = await CreateObjectAsync(token, [], ct);

        using var response = await PostObservationAsync(
            token,
            new Setup(source.SourceId, source.StatusA, source.StatusB, objectId),
            ct: ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("detail").GetString()
            .ShouldNotBeNull().ShouldContain("тип");
        (await CountObservationsAsync(objectId, ct)).ShouldBe(0L);
    }

    [Fact]
    public async Task Site_source_of_other_category_is_rejected_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSiteSetupAsync(token, ct);
        var other = await CreateSiteSetupAsync(token, ct);

        using var response = await PostObservationAsync(
            token,
            setup,
            sourceId: other.SourceId,
            sourceStatusId: other.StatusA,
            ct: ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "sourceId");
        (await CountObservationsAsync(setup.ObjectId, ct)).ShouldBe(0L);
    }

    [Fact]
    public async Task Disabled_site_source_is_rejected_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSiteSetupAsync(token, ct);

        using (var disable = await _api.SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{setup.SourceId}/disable",
            token,
            json: null,
            ct))
        {
            disable.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var response = await PostObservationAsync(token, setup, ct: ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "sourceId");
    }

    [Fact]
    public async Task Status_of_another_source_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);
        var other = await CreateSourceWithStatusesAsync(token, ct);

        using var response = await PostObservationAsync(
            token,
            setup,
            sourceId: setup.SourceId,
            sourceStatusId: other.StatusA,
            ct: ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "sourceStatusId");
        (await CountObservationsAsync(setup.ObjectId, ct)).ShouldBe(0L);
    }

    [Fact]
    public async Task Disabled_source_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);

        using (var disable = await _api.SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{setup.SourceId}/disable",
            token,
            json: null,
            ct))
        {
            disable.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var response = await PostObservationAsync(token, setup, ct: ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "sourceId");
    }

    [Fact]
    public async Task Inactive_object_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(ct, objectActive: false);

        using var response = await PostObservationAsync(token, setup, ct: ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "mapObjectId");
    }

    [Fact]
    public async Task Anonymous_401()
    {
        var ct = CancellationToken.None;
        var setup = await CreateSetupAsync(ct);

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            ObservationsPath,
            accessToken: null,
            ObservationJson(setup),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Comment_over_limit_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);

        using var response = await PostObservationAsync(
            token,
            setup,
            comment: new string('а', 501),
            ct: ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "comment");
    }

    [Fact]
    public async Task Comment_limit_follows_global_setting()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);

        try
        {
            await _api.ExecuteAsync(
                "insert into global_settings (key, value) values ('comment_max_length', '10') "
                + "on conflict (key) do update set value = excluded.value",
                ct);

            using (var tooLong = await PostObservationAsync(
                token,
                setup,
                comment: new string('а', 11),
                ct: ct))
            {
                tooLong.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            }

            using var allowed = await PostObservationAsync(
                token,
                setup,
                comment: new string('а', 10),
                ct: ct);
            allowed.StatusCode.ShouldBe(HttpStatusCode.Created);
        }
        finally
        {
            await _api.ExecuteAsync(
                "delete from global_settings where key = 'comment_max_length'",
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task Client_cannot_set_observed_at()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);

        var body = $$"""
            {"mapObjectId":"{{setup.ObjectId}}","sourceId":"{{setup.SourceId}}",
             "sourceStatusId":"{{setup.StatusA}}","observedAt":"2000-01-01T00:00:00Z"}
            """;

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            ObservationsPath,
            token,
            body,
            ct);

        // Клиентское время контрактом не предусмотрено: поле отклоняется, отметка не создаётся.
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "observedAt");
        (await CountObservationsAsync(setup.ObjectId, ct)).ShouldBe(0L);
    }

    [Fact]
    public async Task Expires_at_equals_now_plus_source_timeout()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(ct, timeoutSeconds: 7200);

        var before = DateTimeOffset.UtcNow;

        using var response = await PostObservationAsync(token, setup, ct: ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        var observation = json.GetProperty("observation");
        var observedAt = observation.GetProperty("observedAt").GetDateTimeOffset();
        var expiresAt = observation.GetProperty("expiresAt").GetDateTimeOffset();

        (expiresAt - observedAt).ShouldBe(TimeSpan.FromHours(2));
        observedAt.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(-5));
        observedAt.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task Moderator_observation_has_kind_moderator_and_equal_priority()
    {
        var ct = CancellationToken.None;
        var moderatorToken = await _api.ModeratorTokenAsync(ct);
        var userToken = await _api.UserTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);

        Guid moderatorObservationId;
        using (var response = await PostObservationAsync(
            moderatorToken,
            setup,
            sourceStatusId: setup.StatusA,
            ct: ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            moderatorObservationId = (await CatalogApi.ReadJsonAsync(response, ct))
                .GetProperty("observation").GetProperty("id").GetGuid();
        }

        // Более свежая пользовательская отметка должна перебить модераторскую.
        await Task.Delay(20, ct);

        Guid userObservationId;
        using (var response = await PostObservationAsync(
            userToken,
            setup,
            sourceStatusId: setup.StatusB,
            ct: ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            userObservationId = (await CatalogApi.ReadJsonAsync(response, ct))
                .GetProperty("observation").GetProperty("id").GetGuid();
        }

        (await ReadSourceTypeAsync(moderatorObservationId, ct)).ShouldBe("moderator");
        (await ReadSourceTypeAsync(userObservationId, ct)).ShouldBe("manual");

        (await _api.CountAsync(
            $"""
             select count(*) from current_statuses
             where map_object_id = '{setup.ObjectId}'
               and source_status_id = '{setup.StatusB}'
               and observation_id = '{userObservationId}'
             """,
            ct)).ShouldBe(1L);
    }

    [Fact]
    public async Task Moderator_observation_is_audited_user_is_not()
    {
        var ct = CancellationToken.None;
        var moderatorToken = await _api.ModeratorTokenAsync(ct);
        var userToken = await _api.UserTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);

        Guid moderatorObservationId;
        using (var response = await PostObservationAsync(moderatorToken, setup, ct: ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            moderatorObservationId = (await CatalogApi.ReadJsonAsync(response, ct))
                .GetProperty("observation").GetProperty("id").GetGuid();
        }

        var audit = await _api.FindAuditAsync(
            "observation.create",
            moderatorObservationId.ToString(),
            ct);

        audit.ShouldNotBeNull("Отметка модератора должна попасть в аудит.");
        audit!.ActorId.ShouldBe(CatalogApi.ModeratorId);
        audit.EntityType.ShouldBe("observation");
        audit.After.ShouldNotBeNull();

        Guid userObservationId;
        using (var response = await PostObservationAsync(userToken, setup, ct: ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            userObservationId = (await CatalogApi.ReadJsonAsync(response, ct))
                .GetProperty("observation").GetProperty("id").GetGuid();
        }

        (await _api.FindAuditAsync(
            "observation.create",
            userObservationId.ToString(),
            ct)).ShouldBeNull("Пользовательская отметка в аудит не пишется.");
    }

    [Fact]
    public async Task Public_list_hides_reporter_and_retracted()
    {
        var ct = CancellationToken.None;
        var moderatorToken = await _api.ModeratorTokenAsync(ct);
        var userToken = await _api.UserTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);

        Guid visibleId;
        using (var response = await PostObservationAsync(
            userToken,
            setup,
            comment: "Вода есть",
            ct: ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            visibleId = (await CatalogApi.ReadJsonAsync(response, ct))
                .GetProperty("observation").GetProperty("id").GetGuid();
        }

        Guid retractedId;
        using (var response = await PostObservationAsync(
            moderatorToken,
            setup,
            sourceStatusId: setup.StatusB,
            comment: "Ошибочная отметка",
            ct: ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            retractedId = (await CatalogApi.ReadJsonAsync(response, ct))
                .GetProperty("observation").GetProperty("id").GetGuid();
        }

        await _api.ExecuteAsync(
            $"""
             update observations
             set is_retracted = true, retracted_by = '{CatalogApi.ModeratorId}',
                 retracted_at = now(), retract_reason = 'Дубликат'
             where id = '{retractedId}'
             """,
            ct);

        using var list = await _api.GetAsync(
            $"{ObservationsOfObjectPath(setup.ObjectId)}",
            accessToken: null,
            ct);

        list.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await CatalogApi.ReadJsonAsync(list, ct);
        var items = json.GetProperty("items").EnumerateArray().ToArray();

        items.Select(item => item.GetProperty("id").GetGuid()).ShouldBe([visibleId]);

        foreach (var item in items)
        {
            item.TryGetProperty("reporterId", out _).ShouldBeFalse();
            item.TryGetProperty("author", out _).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Public_list_shows_raw_message_text_for_source_observations()
    {
        var ct = CancellationToken.None;
        var setup = await CreateSetupAsync(ct);
        const string RawText = "Воды нет с утра, трубу меняют";

        var observationId = await InsertSourceObservationAsync(
            setup,
            RawText,
            postedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            ct);

        using var list = await _api.GetAsync(
            $"{ObservationsOfObjectPath(setup.ObjectId)}",
            accessToken: null,
            ct);

        list.StatusCode.ShouldBe(HttpStatusCode.OK);

        var item = (await CatalogApi.ReadJsonAsync(list, ct))
            .GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == observationId);

        item.GetProperty("text").GetString().ShouldBe(RawText);
        item.GetProperty("sourceType").GetString().ShouldBe("telegram");
        item.GetProperty("status").GetProperty("id").GetGuid().ShouldBe(setup.StatusA);
    }

    [Fact]
    public async Task Public_list_shows_comment_for_user_observations()
    {
        var ct = CancellationToken.None;
        var token = await _api.UserTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);
        const string Comment = "Вода есть, напор хороший";

        Guid observationId;
        using (var response = await PostObservationAsync(token, setup, comment: Comment, ct: ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            observationId = (await CatalogApi.ReadJsonAsync(response, ct))
                .GetProperty("observation").GetProperty("id").GetGuid();
        }

        using var list = await _api.GetAsync(
            $"{ObservationsOfObjectPath(setup.ObjectId)}",
            accessToken: null,
            ct);

        list.StatusCode.ShouldBe(HttpStatusCode.OK);

        var item = (await CatalogApi.ReadJsonAsync(list, ct))
            .GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == observationId);

        item.GetProperty("text").GetString().ShouldBe(Comment);
        item.GetProperty("sourceType").GetString().ShouldBe("manual");
    }

    [Fact]
    public async Task Geo_distance_is_stored_but_does_not_block()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);

        Guid nearId;
        using (var response = await PostObservationAsync(
            token,
            setup,
            latitude: 48.01,
            longitude: 37.80,
            ct: ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            nearId = (await CatalogApi.ReadJsonAsync(response, ct))
                .GetProperty("observation").GetProperty("id").GetGuid();
        }

        var nearDistance = await ReadGeoDistanceAsync(nearId, ct);
        nearDistance.ShouldNotBeNull();
        nearDistance!.Value.ShouldBeInRange(0, 5);

        Guid farId;
        using (var response = await PostObservationAsync(
            token,
            setup,
            latitude: 48.20,
            longitude: 38.00,
            ct: ct))
        {
            // Геолокация — мягкий сигнал: расстояние не блокирует отметку.
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            farId = (await CatalogApi.ReadJsonAsync(response, ct))
                .GetProperty("observation").GetProperty("id").GetGuid();
        }

        (await ReadGeoDistanceAsync(farId, ct)).ShouldNotBeNull().ShouldBeGreaterThan(1000);

        using var invalid = await PostObservationAsync(
            token,
            setup,
            latitude: 120,
            longitude: 37.80,
            ct: ct);

        invalid.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task ObservationCreated_is_enqueued_in_outbox()
    {
        var ct = CancellationToken.None;
        var token = await _api.UserTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);

        Guid observationId;
        using (var response = await PostObservationAsync(token, setup, ct: ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            observationId = (await CatalogApi.ReadJsonAsync(response, ct))
                .GetProperty("observation").GetProperty("id").GetGuid();
        }

        var payload = await ReadOutboxPayloadAsync(observationId, ct);

        payload.ShouldNotBeNull("Событие ObservationCreated должно попасть в outbox.");

        using var document = JsonDocument.Parse(payload!);
        var root = document.RootElement;

        root.GetProperty("observationId").GetGuid().ShouldBe(observationId);
        root.GetProperty("mapObjectId").GetGuid().ShouldBe(setup.ObjectId);
        root.GetProperty("sourceKind").GetString().ShouldBe("manual");
        root.GetProperty("at").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));
        // Группа фиксации не задана — groupId в событии null.
        (root.TryGetProperty("groupId", out var groupId) && groupId.ValueKind != JsonValueKind.Null)
            .ShouldBeFalse();
    }

    [Fact]
    public async Task Rate_limit_exceeded_429()
    {
        var ct = CancellationToken.None;
        var setup = await CreateSetupAsync(ct);

        // Отдельный хост с включённым лимитом: остальные тесты класса лимит не проверяют.
        using var factory = new TestApiFactory(
            fixture.ConnectionString,
            settings: new Dictionary<string, string?>
            {
                ["RateLimiting:Enabled"] = "true",
            });

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });

        using (var health = await client.GetAsync("/health", ct))
        {
            health.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        //: лимит на пользователя берётся из global_settings.
        await _api.ExecuteAsync(
            "insert into global_settings (key, value) values ('observation_rate_per_hour', '1') "
            + "on conflict (key) do update set value = excluded.value",
            ct);

        try
        {
            var token = await TestAuth.CreateAccessTokenAsync(factory.Services, CatalogApi.UserId, ct);

            using (var first = await SendAsync(
                client,
                HttpMethod.Post,
                ObservationsPath,
                token,
                ObservationJson(setup),
                ct))
            {
                first.StatusCode.ShouldBe(HttpStatusCode.Created);
            }

            using var second = await SendAsync(
                client,
                HttpMethod.Post,
                ObservationsPath,
                token,
                ObservationJson(setup),
                ct);

            second.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            second.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
            second.Headers.TryGetValues("Retry-After", out var retryAfter).ShouldBeTrue();
            int.Parse(retryAfter!.Single(), CultureInfo.InvariantCulture).ShouldBeGreaterThan(0);
        }
        finally
        {
            await _api.ExecuteAsync(
                "delete from global_settings where key = 'observation_rate_per_hour'",
                ct);
        }
    }

    // ------------------------------------------------------------------
    // Данные и запросы
    // ------------------------------------------------------------------

    private async Task<Setup> CreateSetupAsync(
        CancellationToken ct,
        bool objectActive = true,
        int timeoutSeconds = 3600)
    {
        var token = await _api.ModeratorTokenAsync(ct);
        var source = await CreateSourceWithStatusesAsync(token, ct, timeoutSeconds);
        var objectId = await CreateObjectAsync(
            token,
            [source.SourceId],
            ct,
            isActive: objectActive);

        return new Setup(source.SourceId, source.StatusA, source.StatusB, objectId);
    }

    /// <summary>
    /// Фиксация категории без привязанных источников: «Сайт» доступен ей неявно.
    /// Категория создаётся через API, статусы справочника заводятся SQL-ом, затем категория
    /// сохраняется повторно — провижионер копирует статусы в «Сайт».
    /// </summary>
    private async Task<Setup> CreateSiteSetupAsync(string token, CancellationToken ct)
    {
        var suffix = CatalogApi.NewSuffix();
        var code = $"sitecat_{suffix}";
        var name = $"Категория сайта {suffix}";

        Guid categoryId;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/categories",
            token,
            $$"""{"code":"{{code}}","name":"{{name}}"}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            categoryId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
        }

        var okCode = $"site_ok_{suffix}";
        var noneCode = $"site_none_{suffix}";

        await _api.ExecuteAsync(
            $"""
             insert into status_definitions
               (category_id, code, name, color, icon, severity, is_positive)
             values
               ('{categoryId}', '{okCode}', 'Вода есть {suffix}', 'ok', 'drop', 0, true),
               ('{categoryId}', '{noneCode}', 'Воды нет {suffix}', 'none', 'cross', 3, false)
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

        Guid objectTypeId;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/object-types",
            token,
            $$"""
              {"categoryId":"{{categoryId}}","code":"sitetype_{{suffix}}",
               "name":"Тип сайта {{suffix}}","geometryKind":"point"}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            objectTypeId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
        }

        var objectId = await CreateObjectOfTypeAsync(token, objectTypeId, ct);
        var (sourceId, statusA, statusB) = await ReadSiteSourceAsync(
            categoryId,
            okCode,
            noneCode,
            ct);

        return new Setup(sourceId, statusA, statusB, objectId);
    }

    private async Task<Guid> CreateObjectOfTypeAsync(
        string token,
        Guid objectTypeId,
        CancellationToken ct)
    {
        var suffix = CatalogApi.NewSuffix();

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            ObjectsPath,
            token,
            $$"""
              {"name":"Фиксация сайта {{suffix}}","objectTypeId":"{{objectTypeId}}",
               "coverageKind":"point","geometry":{"type":"Point","coordinates":[37.80,48.01]},
               "isActive":true,"sourceIds":[]}
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
    }

    private async Task<(Guid SourceId, Guid StatusA, Guid StatusB)> ReadSiteSourceAsync(
        Guid categoryId,
        string okCode,
        string noneCode,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select s.id,
                   (select id from source_statuses where source_id = s.id and code = @ok),
                   (select id from source_statuses where source_id = s.id and code = @none)
            from sources s
            where s.category_id = @category and s.source_type = 'site'
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("category", NpgsqlDbType.Uuid) { Value = categoryId });
        command.Parameters.Add(new NpgsqlParameter("ok", NpgsqlDbType.Text) { Value = okCode });
        command.Parameters.Add(new NpgsqlParameter("none", NpgsqlDbType.Text) { Value = noneCode });

        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return (reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2));
    }

    private async Task<SourceSetup> CreateSourceWithStatusesAsync(
        string token,
        CancellationToken ct,
        int timeoutSeconds = 3600)
    {
        var suffix = CatalogApi.NewSuffix();

        Guid sourceId;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            SourcesPath,
            token,
            $$"""
              {"name":"Источник {{suffix}}","sourceType":"telegram_chat",
               "chatId":"@obs_{{suffix}}","timeoutSeconds":{{timeoutSeconds.ToString(CultureInfo.InvariantCulture)}},
               "userPrompt":""}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            sourceId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
        }

        var statusA = await CreateStatusAsync(token, sourceId, $"{suffix}a", $"Вода есть {suffix}", ct);
        var statusB = await CreateStatusAsync(token, sourceId, $"{suffix}b", $"Воды нет {suffix}", ct);

        return new SourceSetup(sourceId, statusA, statusB);
    }

    private async Task<Guid> CreateStatusAsync(
        string token,
        Guid sourceId,
        string suffix,
        string name,
        CancellationToken ct)
    {
        using var response = await _api.SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{sourceId}/statuses",
            token,
            $$"""
              {"code":"water_{{suffix}}","name":"{{name}}","description":"Подача идёт",
               "color":"ok","icon":"drop"}
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateObjectAsync(
        string token,
        IReadOnlyList<Guid> sourceIds,
        CancellationToken ct,
        bool isActive = true)
    {
        var suffix = CatalogApi.NewSuffix();
        var links = string.Join(",", sourceIds.Select(id => $"\"{id}\""));

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            ObjectsPath,
            token,
            $$"""
              {"name":"Фиксация {{suffix}}","objectTypeId":"00000000-0000-0000-0000-00000000aa01","coverageKind":"point",
               "geometry":{"type":"Point","coordinates":[37.80,48.01]},
               "isActive":{{isActive.ToString().ToLowerInvariant()}},"sourceIds":[{{links}}]}
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> PostObservationAsync(
        string token,
        Setup setup,
        Guid? sourceId = null,
        Guid? sourceStatusId = null,
        string? comment = null,
        double? latitude = null,
        double? longitude = null,
        CancellationToken ct = default)
    {
        var body = ObservationJson(
            setup,
            sourceId,
            sourceStatusId,
            comment,
            latitude,
            longitude);

        return _api.SendAsync(HttpMethod.Post, ObservationsPath, token, body, ct);
    }

    private static string ObservationJson(
        Setup setup,
        Guid? sourceId = null,
        Guid? sourceStatusId = null,
        string? comment = null,
        double? latitude = null,
        double? longitude = null)
    {
        var commentPart = comment is null
            ? string.Empty
            : $",\"comment\":{JsonSerializer.Serialize(comment)}";

        var locationPart = latitude is { } lat && longitude is { } lon
            ? $",\"location\":{{\"lat\":{lat.ToString(CultureInfo.InvariantCulture)},"
              + $"\"lon\":{lon.ToString(CultureInfo.InvariantCulture)}}}"
            : string.Empty;

        return $$"""
            {"mapObjectId":"{{setup.ObjectId}}",
             "sourceId":"{{sourceId ?? setup.SourceId}}",
             "sourceStatusId":"{{sourceStatusId ?? setup.StatusA}}"{{commentPart}}{{locationPart}}}
            """;
    }

    private async Task<Guid> InsertSourceObservationAsync(
        Setup setup,
        string text,
        DateTimeOffset postedAt,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        var messageId = Guid.NewGuid();

        await using (var message = new NpgsqlCommand(
            """
            insert into raw_messages (id, source_id, external_id, text, posted_at)
            values (@id, @source, @external, @text, @posted_at)
            """,
            connection))
        {
            message.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = messageId });
            message.Parameters.Add(new NpgsqlParameter("source", NpgsqlDbType.Uuid) { Value = setup.SourceId });
            message.Parameters.Add(new NpgsqlParameter("external", NpgsqlDbType.Text)
            {
                Value = $"ext-{Guid.NewGuid():N}",
            });
            message.Parameters.Add(new NpgsqlParameter("text", NpgsqlDbType.Text) { Value = text });
            message.Parameters.Add(new NpgsqlParameter("posted_at", NpgsqlDbType.TimestampTz) { Value = postedAt });

            await message.ExecuteNonQueryAsync(ct);
        }

        var observedAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        await using var observation = new NpgsqlCommand(
            """
            insert into observations
              (map_object_id, source_id, source_status_id, source_type, observed_at,
               received_at, expires_at, raw_message_id)
            values
              (@map_object, @source, @status, 'telegram', @observed_at,
               @observed_at, @expires_at, @raw_message)
            returning id
            """,
            connection);
        observation.Parameters.Add(new NpgsqlParameter("map_object", NpgsqlDbType.Uuid)
        {
            Value = setup.ObjectId,
        });
        observation.Parameters.Add(new NpgsqlParameter("source", NpgsqlDbType.Uuid) { Value = setup.SourceId });
        observation.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Uuid) { Value = setup.StatusA });
        observation.Parameters.Add(new NpgsqlParameter("observed_at", NpgsqlDbType.TimestampTz)
        {
            Value = observedAt,
        });
        observation.Parameters.Add(new NpgsqlParameter("expires_at", NpgsqlDbType.TimestampTz)
        {
            Value = observedAt.AddHours(1),
        });
        observation.Parameters.Add(new NpgsqlParameter("raw_message", NpgsqlDbType.Uuid)
        {
            Value = messageId,
        });

        return (Guid)(await observation.ExecuteScalarAsync(ct))!;
    }

    private async Task<ObservationRow?> ReadObservationRowAsync(Guid observationId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            "select observed_at, expires_at, source_type from observations where id = @id",
            connection);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = observationId });

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new ObservationRow(
            ReadTimestamp(reader, 0),
            ReadTimestamp(reader, 1),
            reader.GetString(2));
    }

    private async Task<string?> ReadSourceTypeAsync(Guid observationId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            "select source_type from observations where id = @id",
            connection);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = observationId });

        return (string?)await command.ExecuteScalarAsync(ct);
    }

    private async Task<int?> ReadGeoDistanceAsync(Guid observationId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            "select geo_distance_m from observations where id = @id",
            connection);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = observationId });

        var value = await command.ExecuteScalarAsync(ct);

        return value is null or DBNull
            ? null
            : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private async Task<string?> ReadOutboxPayloadAsync(Guid observationId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select payload::text
            from outbox_messages
            where type = 'ObservationCreated'
              and payload ->> 'observationId' = @observation_id
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("observation_id", NpgsqlDbType.Text)
        {
            Value = observationId.ToString(),
        });

        return (string?)await command.ExecuteScalarAsync(ct);
    }

    private Task<long> CountOutboxForObjectAsync(Guid mapObjectId, CancellationToken ct) =>
        _api.CountAsync(
            $"""
             select count(*) from outbox_messages
             where type = 'ObservationCreated'
               and payload ->> 'mapObjectId' = '{mapObjectId}'
             """,
            ct);

    private Task<long> CountObservationsAsync(Guid mapObjectId, CancellationToken ct) =>
        _api.CountAsync(
            $"select count(*) from observations where map_object_id = '{mapObjectId}'",
            ct);

    private static string ObservationsOfObjectPath(Guid mapObjectId) =>
        $"/api/v1/objects/{mapObjectId}/observations";

    private static DateTimeOffset ReadTimestamp(NpgsqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetFieldValue<DateTime>(ordinal), DateTimeKind.Utc));

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? accessToken,
        string? json,
        CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, path);

        if (accessToken is not null)
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        return client.SendAsync(request, ct);
    }

    private sealed record Setup(Guid SourceId, Guid StatusA, Guid StatusB, Guid ObjectId);

    private sealed record SourceSetup(Guid SourceId, Guid StatusA, Guid StatusB);

    private sealed record ObservationRow(
        DateTimeOffset ObservedAt,
        DateTimeOffset ExpiresAt,
        string SourceType);

    /// <summary>Outbox, который падает на enqueue: проверка атомарности транзакции отметки.</summary>
    private sealed class ThrowingOutbox : IOutbox
    {
        public void Enqueue(string destination, string type, object payload) =>
            throw new InvalidOperationException("Тестовый сбой outbox");
    }
}
