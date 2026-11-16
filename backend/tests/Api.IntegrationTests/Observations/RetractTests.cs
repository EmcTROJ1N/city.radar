using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Api.IntegrationTests.Catalog;
using Api.IntegrationTests.Identity;
using Api.IntegrationTests.Infrastructure;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;
using NpgsqlTypes;

using Observations.Application;

using Shouldly;

namespace Api.IntegrationTests.Observations;

/// <summary>
/// Отзыв наблюдения — одиночный и пакетный:
/// немедленный пересчёт проекции, повторный отзыв, права, обязательная причина, аудит с
/// before/after, пакетный отбор по автору/источнику/периоду и откат всей операции при сбое.
/// Хост Api поднимается на общей БД коллекции; изоляция — новые источники, статусы, фиксации
/// и наблюдения на каждый тест.
/// </summary>
[Collection("postgres")]
public sealed class RetractTests(PostgresFixture fixture) : IAsyncLifetime
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
    public async Task Retract_winning_observation_recalculates_immediately()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);

        var olderId = await PostObservationAsync(token, setup, setup.StatusA, ct);
        await Task.Delay(30, ct);
        var newerId = await PostObservationAsync(token, setup, setup.StatusB, ct);

        // Исходно побеждает более свежее наблюдение.
        (await ReadProjectionAsync(setup.ObjectId, ct)).StatusId.ShouldBe(setup.StatusB);

        using var response = await RetractAsync(newerId, token, "Ошибочная отметка", ct);

        //: проекция пересчитана тем же запросом, победило предыдущее актуальное.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        json.GetProperty("status").GetProperty("sourceStatusId").GetGuid().ShouldBe(setup.StatusA);
        json.GetProperty("status").GetProperty("observationCount").GetInt32().ShouldBe(1);

        var projection = await ReadProjectionAsync(setup.ObjectId, ct);
        projection.StatusId.ShouldBe(setup.StatusA);
        projection.ObservationCount.ShouldBe(1);

        (await _api.CountAsync(
            $"select count(*) from observations where id = '{newerId}' and is_retracted",
            ct)).ShouldBe(1L);

        // Предыдущее наблюдение не тронуто.
        (await _api.CountAsync(
            $"select count(*) from observations where id = '{olderId}' and not is_retracted",
            ct)).ShouldBe(1L);
    }

    [Fact]
    public async Task Retract_of_single_actual_observation_gives_unknown_and_stale()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);
        var observationId = await PostObservationAsync(token, setup, setup.StatusA, ct);

        using var response = await RetractAsync(observationId, token, "Дубликат", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        json.GetProperty("status").ValueKind.ShouldBe(JsonValueKind.Null);

        var projection = await ReadProjectionAsync(setup.ObjectId, ct);
        projection.StatusId.ShouldBeNull();
        projection.Freshness.ShouldBe("stale");
        projection.ObservationCount.ShouldBe(0);
    }

    [Fact]
    public async Task Retract_twice_409()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);
        var observationId = await PostObservationAsync(token, setup, setup.StatusA, ct);

        using (var first = await RetractAsync(observationId, token, "Дубликат", ct))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var second = await RetractAsync(observationId, token, "Ещё раз", ct);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        second.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Retract_without_reason_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);
        var observationId = await PostObservationAsync(token, setup, setup.StatusA, ct);

        using (var missing = await SendAsync(
            HttpMethod.Post,
            $"{ObservationsPath}/{observationId}/retract",
            token,
            "{}",
            ct))
        {
            missing.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await CatalogApi.ReadJsonAsync(missing, ct)).GetProperty("errors").EnumerateArray()
                .ShouldContain(error => error.GetProperty("field").GetString() == "reason");
        }

        using var empty = await RetractAsync(observationId, token, reason: string.Empty, ct);

        empty.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        (await _api.CountAsync(
            $"select count(*) from observations where id = '{observationId}' and is_retracted",
            ct)).ShouldBe(0L);
    }

    [Fact]
    public async Task Retract_by_user_403()
    {
        var ct = CancellationToken.None;
        var userToken = await _api.UserTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);
        var observationId = await PostObservationAsync(userToken, setup, setup.StatusA, ct);

        using var response = await RetractAsync(observationId, userToken, "Хочу отозвать", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _api.CountAsync(
            $"select count(*) from observations where id = '{observationId}' and is_retracted",
            ct)).ShouldBe(0L);
    }

    [Fact]
    public async Task Retract_anonymous_401()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);
        var observationId = await PostObservationAsync(token, setup, setup.StatusA, ct);

        using var response = await RetractAsync(observationId, accessToken: null, "Причина", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Retract_is_audited_with_before_after_and_reason()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);
        var observationId = await PostObservationAsync(token, setup, setup.StatusA, ct);
        const string Reason = "Ложные данные от жителя";

        using (var response = await RetractAsync(observationId, token, Reason, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var audit = await FindRetractAuditAsync(observationId, ct);

        audit.ShouldNotBeNull("Отзыв должен попасть в аудит.");
        audit!.ActorId.ShouldBe(CatalogApi.ModeratorId);
        audit.EntityType.ShouldBe("observation");
        audit.Reason.ShouldBe(Reason);
        audit.Before.ShouldNotBeNull();
        audit.After.ShouldNotBeNull();
        audit.Before!.Value.GetProperty("isRetracted").GetBoolean().ShouldBeFalse();
        audit.After!.Value.GetProperty("isRetracted").GetBoolean().ShouldBeTrue();
        audit.After!.Value.GetProperty("reason").GetString().ShouldBe(Reason);
        audit.After!.Value.GetProperty("mapObjectId").GetGuid().ShouldBe(setup.ObjectId);
    }

    [Fact]
    public async Task Retract_rolls_back_all_on_failure()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);
        var observationId = await PostObservationAsync(token, setup, setup.StatusA, ct);

        // Подмена пересчёта: отзыв и аудит должны откатиться вместе с проекцией.
        using var factory = new TestApiFactory(fixture.ConnectionString);
        using var failing = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IStatusRecalculator>();
            services.AddScoped<IStatusRecalculator, ThrowingStatusRecalculator>();
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

        var failingToken = await TestAuth.CreateAccessTokenAsync(failing.Services, CatalogApi.ModeratorId, ct);

        using (var response = await SendAsync(
            HttpMethod.Post,
            $"{ObservationsPath}/{observationId}/retract",
            failingToken,
            """{"reason":"Сбойный отзыв"}""",
            ct,
            client))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        }

        (await _api.CountAsync(
            $"select count(*) from observations where id = '{observationId}' and not is_retracted",
            ct)).ShouldBe(1L);

        (await ReadProjectionAsync(setup.ObjectId, ct)).StatusId.ShouldBe(setup.StatusA);

        (await _api.CountAsync(
            $"select count(*) from audit_entries where action = 'observation.retract' "
            + $"and entity_id = '{observationId}'",
            ct)).ShouldBe(0L);
    }

    [Fact]
    public async Task Retracted_hidden_from_public_list()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);

        var visibleId = await PostObservationAsync(token, setup, setup.StatusA, ct);
        await Task.Delay(20, ct);
        var retractedId = await PostObservationAsync(token, setup, setup.StatusB, ct);

        using (var response = await RetractAsync(retractedId, token, "Ошибочная отметка", ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var list = await _api.GetAsync(
            $"/api/v1/objects/{setup.ObjectId}/observations",
            accessToken: null,
            ct);

        list.StatusCode.ShouldBe(HttpStatusCode.OK);

        var ids = (await CatalogApi.ReadJsonAsync(list, ct))
            .GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .ToArray();

        ids.ShouldContain(visibleId);
        ids.ShouldNotContain(retractedId);
    }

    [Fact]
    public async Task Bulk_retract_by_reporter_recalculates_all_affected_objects()
    {
        var ct = CancellationToken.None;
        var moderatorToken = await _api.ModeratorTokenAsync(ct);
        var userToken = await _api.UserTokenAsync(ct);
        var source = await CreateSourceWithStatusesAsync(moderatorToken, ct);
        var firstObjectId = await CreateObjectAsync(moderatorToken, [source.SourceId], ct);
        var secondObjectId = await CreateObjectAsync(moderatorToken, [source.SourceId], ct);

        var from = DateTimeOffset.UtcNow;

        await PostObservationAsync(
            userToken,
            new Setup(source.SourceId, source.StatusA, source.StatusB, firstObjectId),
            source.StatusA,
            ct);
        await PostObservationAsync(
            userToken,
            new Setup(source.SourceId, source.StatusA, source.StatusB, secondObjectId),
            source.StatusA,
            ct);

        using var response = await SendAsync(
            HttpMethod.Post,
            $"{ObservationsPath}/retract-bulk",
            moderatorToken,
            BulkJson("Массовый отзыв спама", reporterId: CatalogApi.UserId, source.SourceId, from, to: null),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("retractedCount").GetInt32()
            .ShouldBe(2);

        // Проекция обеих затронутых фиксаций пересчитана в той же транзакции.
        (await ReadProjectionAsync(firstObjectId, ct)).StatusId.ShouldBeNull();
        (await ReadProjectionAsync(secondObjectId, ct)).StatusId.ShouldBeNull();
    }

    [Fact]
    public async Task Bulk_empty_filter_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);
        await PostObservationAsync(token, setup, setup.StatusA, ct);

        using var response = await SendAsync(
            HttpMethod.Post,
            $"{ObservationsPath}/retract-bulk",
            token,
            """{"reason":"Отозвать всё"}""",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "filters");

        (await ReadProjectionAsync(setup.ObjectId, ct)).StatusId.ShouldBe(setup.StatusA);
    }

    [Fact]
    public async Task Bulk_by_source_and_period()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);

        // Старое наблюдение источника (два дня назад) — вне периода и не отзывается.
        var oldObservationId = await InsertTelegramObservationAsync(
            setup,
            "Старое сообщение",
            DateTimeOffset.UtcNow.AddDays(-2),
            ct);

        var freshObservationId = await PostObservationAsync(token, setup, setup.StatusA, ct);

        var from = DateTimeOffset.UtcNow.AddHours(-1);
        var to = DateTimeOffset.UtcNow.AddHours(1);

        using var response = await SendAsync(
            HttpMethod.Post,
            $"{ObservationsPath}/retract-bulk",
            token,
            BulkJson("Отзыв за период", reporterId: null, setup.SourceId, from, to),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("retractedCount").GetInt32()
            .ShouldBe(1);

        (await IsRetractedAsync(freshObservationId, ct)).ShouldBeTrue();
        (await IsRetractedAsync(oldObservationId, ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Bulk_counts_only_not_yet_retracted()
    {
        var ct = CancellationToken.None;
        var moderatorToken = await _api.ModeratorTokenAsync(ct);
        var userToken = await _api.UserTokenAsync(ct);
        var setup = await CreateSetupAsync(ct);

        var from = DateTimeOffset.UtcNow;

        var firstId = await PostObservationAsync(userToken, setup, setup.StatusA, ct);
        await PostObservationAsync(userToken, setup, setup.StatusB, ct);

        using (var single = await RetractAsync(firstId, moderatorToken, "Уже отозвано", ct))
        {
            single.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var response = await SendAsync(
            HttpMethod.Post,
            $"{ObservationsPath}/retract-bulk",
            moderatorToken,
            BulkJson("Пакетный отзыв", CatalogApi.UserId, setup.SourceId, from, to: null),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("retractedCount").GetInt32()
            .ShouldBe(1);
    }

    // ------------------------------------------------------------------
    // Данные и запросы
    // ------------------------------------------------------------------

    private async Task<Setup> CreateSetupAsync(CancellationToken ct, int timeoutSeconds = 3600)
    {
        var token = await _api.ModeratorTokenAsync(ct);
        var source = await CreateSourceWithStatusesAsync(token, ct, timeoutSeconds);
        var objectId = await CreateObjectAsync(token, [source.SourceId], ct);

        return new Setup(source.SourceId, source.StatusA, source.StatusB, objectId);
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
               "chatId":"@retract_{{suffix}}","timeoutSeconds":{{timeoutSeconds.ToString(CultureInfo.InvariantCulture)}},
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
        CancellationToken ct)
    {
        var suffix = CatalogApi.NewSuffix();
        var links = string.Join(",", sourceIds.Select(id => $"\"{id}\""));

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            ObjectsPath,
            token,
            $$"""
              {"name":"Фиксация {{suffix}}","coverageKind":"point",
               "geometry":{"type":"Point","coordinates":[37.80,48.01]},
               "isActive":true,"sourceIds":[{{links}}]}
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> PostObservationAsync(
        string token,
        Setup setup,
        Guid sourceStatusId,
        CancellationToken ct)
    {
        using var response = await _api.SendAsync(
            HttpMethod.Post,
            ObservationsPath,
            token,
            $$"""
              {"mapObjectId":"{{setup.ObjectId}}","sourceId":"{{setup.SourceId}}",
               "sourceStatusId":"{{sourceStatusId}}"}
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await CatalogApi.ReadJsonAsync(response, ct))
            .GetProperty("observation").GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> RetractAsync(
        Guid observationId,
        string? accessToken,
        string reason,
        CancellationToken ct) =>
        SendAsync(
            HttpMethod.Post,
            $"{ObservationsPath}/{observationId}/retract",
            accessToken,
            JsonSerializer.Serialize(new { reason }),
            ct);

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? accessToken,
        string json,
        CancellationToken ct,
        HttpClient? httpClient = null)
    {
        var request = new HttpRequestMessage(method, path);

        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        return (httpClient ?? _api.Client).SendAsync(request, ct);
    }

    private static string BulkJson(
        string reason,
        Guid? reporterId,
        Guid? sourceId,
        DateTimeOffset? from,
        DateTimeOffset? to)
    {
        var parts = new List<string> { $"\"reason\":{JsonSerializer.Serialize(reason)}" };

        if (reporterId is { } reporter)
        {
            parts.Add($"\"reporterId\":\"{reporter}\"");
        }

        if (sourceId is { } source)
        {
            parts.Add($"\"sourceId\":\"{source}\"");
        }

        if (from is { } fromValue)
        {
            parts.Add($"\"from\":\"{fromValue:O}\"");
        }

        if (to is { } toValue)
        {
            parts.Add($"\"to\":\"{toValue:O}\"");
        }

        return "{" + string.Join(',', parts) + "}";
    }

    /// <summary>Вставляет наблюдение источника с текстом исходного сообщения.</summary>
    private async Task<Guid> InsertTelegramObservationAsync(
        Setup setup,
        string text,
        DateTimeOffset observedAt,
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
            message.Parameters.Add(new NpgsqlParameter("posted_at", NpgsqlDbType.TimestampTz) { Value = observedAt });

            await message.ExecuteNonQueryAsync(ct);
        }

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

    private async Task<ProjectionRow> ReadProjectionAsync(Guid mapObjectId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select source_status_id, freshness, observation_count
            from current_statuses
            where map_object_id = @map_object_id
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("map_object_id", NpgsqlDbType.Uuid)
        {
            Value = mapObjectId,
        });

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new ProjectionRow(null, null, 0);
        }

        return new ProjectionRow(
            reader.IsDBNull(0) ? null : reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt32(2));
    }

    private async Task<bool> IsRetractedAsync(Guid observationId, CancellationToken ct) =>
        await _api.CountAsync(
            $"select count(*) from observations where id = '{observationId}' and is_retracted",
            ct) == 1L;

    private async Task<RetractAuditRow?> FindRetractAuditAsync(
        Guid observationId,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select actor_id, entity_type, reason, before::text, after::text
            from audit_entries
            where action = 'observation.retract' and entity_id = @entity_id
            order by id desc
            limit 1
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("entity_id", NpgsqlDbType.Text)
        {
            Value = observationId.ToString(),
        });

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new RetractAuditRow(
            reader.IsDBNull(0) ? null : reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            ParseJson(reader.IsDBNull(3) ? null : reader.GetString(3)),
            ParseJson(reader.IsDBNull(4) ? null : reader.GetString(4)));
    }

    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }

    private sealed record Setup(Guid SourceId, Guid StatusA, Guid StatusB, Guid ObjectId);

    private sealed record SourceSetup(Guid SourceId, Guid StatusA, Guid StatusB);

    private sealed record ProjectionRow(Guid? StatusId, string? Freshness, int ObservationCount);

    private sealed record RetractAuditRow(
        Guid? ActorId,
        string? EntityType,
        string? Reason,
        JsonElement? Before,
        JsonElement? After);

    /// <summary>Пересчёт, который падает: проверка атомарности отзыва.</summary>
    private sealed class ThrowingStatusRecalculator : IStatusRecalculator
    {
        public Task RecalculateAsync(Guid mapObjectId, DateTimeOffset now, CancellationToken ct) =>
            throw new InvalidOperationException("Тестовый сбой пересчёта");
    }
}
