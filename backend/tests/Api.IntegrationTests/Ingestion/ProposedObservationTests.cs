using System.Globalization;
using System.Net;
using System.Text.Json;

using Api.IntegrationTests.Catalog;
using Api.IntegrationTests.Infrastructure;

using Npgsql;
using NpgsqlTypes;

using Shouldly;

namespace Api.IntegrationTests.Ingestion;

/// <summary>
/// Приём предложений Worker-а в Api: outbox-сообщение
/// <c>ObservationProposed</c> → наблюдение → проекция <c>current_statuses</c>, идемпотентность
/// по id сообщения, пропуск выключенных источников и неактивных фиксаций, старое сообщение.
/// </summary>
/// <remarks>
/// БД общая на коллекцию; <c>outbox_messages</c> очищается перед каждым тестом, чтобы
/// <see cref="BuildingBlocks.Messaging.OutboxDispatcher"/> обрабатывал только сообщения теста.
/// Источники, статусы, фиксации и сообщения уникальны.
/// </remarks>
[Collection("postgres")]
public sealed class ProposedObservationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string SourcesPath = "/api/v1/sources";
    private const string ObjectsPath = "/api/v1/catalog/objects";

    private CatalogApi _api = null!;

    public async Task InitializeAsync()
    {
        _api = new CatalogApi(fixture);
        await _api.InitializeAsync();

        await _api.ExecuteAsync("truncate table outbox_messages;");
    }

    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task Proposed_observation_reaches_map()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(token, ct);
        var observedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var rawMessageId = await InsertRawMessageAsync(
            setup.SourceId,
            "Воду дали",
            observedAt,
            ct);

        var messageId = await EnqueueProposedAsync(
            setup,
            rawMessageId,
            observedAt,
            statusCode: setup.StatusCode,
            confidence: 0.93,
            ct);

        var processed = await _api.GetApiDispatcher().DispatchOnceAsync(ct);
        processed.ShouldBe(1);

        var observation = await ReadObservationAsync(setup.ObjectId, ct);
        observation.ShouldNotBeNull();
        observation!.SourceId.ShouldBe(setup.SourceId);
        observation.SourceStatusId.ShouldBe(setup.StatusId);
        observation.SourceType.ShouldBe("telegram");
        observation.RawMessageId.ShouldBe(rawMessageId);
        observation.ObservedAt.ShouldBeInRange(
            observedAt.AddSeconds(-1),
            observedAt.AddSeconds(1));
        observation.ObservedAt.ShouldBeLessThan(DateTimeOffset.UtcNow);
        observation.Confidence.ShouldNotBeNull().ShouldBe(0.93f, 0.0001f);

        //: наблюдение сразу видно в проекции, которую читает карта.
        var status = await ReadCurrentStatusAsync(setup.ObjectId, ct);
        status.ShouldNotBeNull();
        status!.SourceStatusId.ShouldBe(setup.StatusId);
        status.ObservationCount.ShouldBe(1);
        status.Freshness.ShouldBe("fresh");

        (await ReadProcessedAtAsync(messageId, ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Duplicate_delivery_creates_one_observation()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(token, ct);
        var observedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var rawMessageId = await InsertRawMessageAsync(setup.SourceId, "Вода есть", observedAt, ct);
        var messageId = await EnqueueProposedAsync(
            setup,
            rawMessageId,
            observedAt,
            setup.StatusCode,
            confidence: 0.9,
            ct);

        (await _api.GetApiDispatcher().DispatchOnceAsync(ct)).ShouldBe(1);
        (await CountObservationsAsync(setup.ObjectId, ct)).ShouldBe(1L);

        // At-least-once: сообщение доставлено повторно (processed_at сброшен).
        await _api.ExecuteAsync(
            $"update outbox_messages set processed_at = null, available_at = now() where id = '{messageId}'",
            ct);

        (await _api.GetApiDispatcher().DispatchOnceAsync(ct)).ShouldBe(1);
        (await CountObservationsAsync(setup.ObjectId, ct)).ShouldBe(1L);
    }

    [Fact]
    public async Task Disabled_source_skipped()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(token, ct);
        var observedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var rawMessageId = await InsertRawMessageAsync(setup.SourceId, "Вода есть", observedAt, ct);
        var messageId = await EnqueueProposedAsync(
            setup,
            rawMessageId,
            observedAt,
            setup.StatusCode,
            confidence: 0.9,
            ct);

        using (var disable = await _api.SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{setup.SourceId}/disable",
            token,
            json: null,
            ct))
        {
            disable.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await _api.GetApiDispatcher().DispatchOnceAsync(ct)).ShouldBe(1);

        (await CountObservationsAsync(setup.ObjectId, ct)).ShouldBe(0L);
        (await ReadProcessedAtAsync(messageId, ct)).ShouldNotBeNull(
            "Пропущенное сообщение должно быть помечено обработанным, а не повторяться вечно.");
    }

    [Fact]
    public async Task Deactivated_object_skipped()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(token, ct);
        var observedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var rawMessageId = await InsertRawMessageAsync(setup.SourceId, "Вода есть", observedAt, ct);
        var messageId = await EnqueueProposedAsync(
            setup,
            rawMessageId,
            observedAt,
            setup.StatusCode,
            confidence: 0.9,
            ct);

        await _api.ExecuteAsync(
            $"update map_objects set is_active = false where id = '{setup.ObjectId}'",
            ct);

        (await _api.GetApiDispatcher().DispatchOnceAsync(ct)).ShouldBe(1);

        (await CountObservationsAsync(setup.ObjectId, ct)).ShouldBe(0L);
        (await ReadProcessedAtAsync(messageId, ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Old_message_does_not_affect_status_but_is_saved()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(token, ct, timeoutSeconds: 3600);
        var observedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var rawMessageId = await InsertRawMessageAsync(setup.SourceId, "Старое сообщение", observedAt, ct);
        var messageId = await EnqueueProposedAsync(
            setup,
            rawMessageId,
            observedAt,
            setup.StatusCode,
            confidence: 0.9,
            ct);

        (await _api.GetApiDispatcher().DispatchOnceAsync(ct)).ShouldBe(1);

        var observation = await ReadObservationAsync(setup.ObjectId, ct);
        observation.ShouldNotBeNull("Старое сообщение сохраняется для истории.");
        observation!.ExpiresAt.ShouldBeLessThan(DateTimeOffset.UtcNow);

        var status = await ReadCurrentStatusAsync(setup.ObjectId, ct);
        status.ShouldNotBeNull();
        status!.SourceStatusId.ShouldBeNull("Протухшее наблюдение не выставляет статус.");
        status.Freshness.ShouldBe("stale");
        status.ObservationCount.ShouldBe(0);
        (await ReadProcessedAtAsync(messageId, ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Unknown_status_code_skipped()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(token, ct);
        var observedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var rawMessageId = await InsertRawMessageAsync(setup.SourceId, "Что-то", observedAt, ct);
        var messageId = await EnqueueProposedAsync(
            setup,
            rawMessageId,
            observedAt,
            statusCode: "unknown_status",
            confidence: 0.9,
            ct);

        (await _api.GetApiDispatcher().DispatchOnceAsync(ct)).ShouldBe(1);

        (await CountObservationsAsync(setup.ObjectId, ct)).ShouldBe(0L);
        (await ReadProcessedAtAsync(messageId, ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Proposed_for_two_objects_creates_two_observations_idempotently()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(token, ct);
        var secondObjectId = await CreateObjectAsync(token, [setup.SourceId], ct);
        var observedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var rawMessageId = await InsertRawMessageAsync(setup.SourceId, "Вода по району", observedAt, ct);

        var firstMessageId = await EnqueueProposedAsync(
            setup,
            rawMessageId,
            observedAt,
            setup.StatusCode,
            confidence: 0.9,
            ct);
        var secondMessageId = await EnqueueProposedAsync(
            setup with { ObjectId = secondObjectId },
            rawMessageId,
            observedAt,
            setup.StatusCode,
            confidence: 0.9,
            ct);

        (await _api.GetApiDispatcher().DispatchOnceAsync(ct)).ShouldBe(2);

        (await CountObservationsAsync(setup.ObjectId, ct)).ShouldBe(1L);
        (await CountObservationsAsync(secondObjectId, ct)).ShouldBe(1L);

        // Повторная доставка обоих сообщений не удваивает наблюдения.
        await _api.ExecuteAsync(
            $"""
             update outbox_messages
             set processed_at = null, available_at = now()
             where id in ('{firstMessageId}', '{secondMessageId}')
             """,
            ct);

        (await _api.GetApiDispatcher().DispatchOnceAsync(ct)).ShouldBe(2);

        (await CountObservationsAsync(setup.ObjectId, ct)).ShouldBe(1L);
        (await CountObservationsAsync(secondObjectId, ct)).ShouldBe(1L);

        (await _api.CountAsync(
            $"""
             select count(*) from current_statuses
             where map_object_id in ('{setup.ObjectId}', '{secondObjectId}')
               and source_status_id = '{setup.StatusId}'
             """,
            ct)).ShouldBe(2L);
    }

    // ------------------------------------------------------------------
    // Данные, сообщения и проверки
    // ------------------------------------------------------------------

    private async Task<Setup> CreateSetupAsync(
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
               "chatId":"@ing_{{suffix}}","timeoutSeconds":{{timeoutSeconds.ToString(CultureInfo.InvariantCulture)}},
               "userPrompt":""}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            sourceId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
        }

        Guid statusId;
        string statusCode;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{sourceId}/statuses",
            token,
            $$"""
              {"code":"water_{{suffix}}","name":"Вода есть {{suffix}}",
               "description":"Подача идёт","color":"ok","icon":"drop"}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);

            var json = await CatalogApi.ReadJsonAsync(response, ct);
            statusId = json.GetProperty("id").GetGuid();
            statusCode = json.GetProperty("code").GetString()!;
        }

        var objectId = await CreateObjectAsync(token, [sourceId], ct);

        return new Setup(sourceId, statusId, statusCode, objectId);
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
               "sourceIds":[{{links}}]}
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> InsertRawMessageAsync(
        Guid sourceId,
        string text,
        DateTimeOffset postedAt,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            insert into raw_messages (source_id, external_id, text, posted_at)
            values (@source_id, @external_id, @text, @posted_at)
            returning id
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("source_id", NpgsqlDbType.Uuid) { Value = sourceId });
        command.Parameters.Add(new NpgsqlParameter("external_id", NpgsqlDbType.Text)
        {
            Value = Guid.NewGuid().ToString("N"),
        });
        command.Parameters.Add(new NpgsqlParameter("text", NpgsqlDbType.Text) { Value = text });
        command.Parameters.Add(new NpgsqlParameter("posted_at", NpgsqlDbType.TimestampTz)
        {
            Value = postedAt,
        });

        return (Guid)(await command.ExecuteScalarAsync(ct))!;
    }

    private async Task<Guid> EnqueueProposedAsync(
        Setup setup,
        Guid rawMessageId,
        DateTimeOffset observedAt,
        string statusCode,
        double confidence,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            sourceId = setup.SourceId,
            mapObjectId = setup.ObjectId,
            statusCode,
            rawMessageId,
            observedAt,
            confidence,
        });

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            insert into outbox_messages (destination, type, payload)
            values ('api', 'ObservationProposed', @payload)
            returning id
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payload });

        return (Guid)(await command.ExecuteScalarAsync(ct))!;
    }

    private async Task<ObservationRow?> ReadObservationAsync(
        Guid mapObjectId,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select source_id, source_status_id, source_type, raw_message_id,
                   observed_at, expires_at, confidence
            from observations
            where map_object_id = @map_object_id
            order by observed_at desc
            limit 1
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("map_object_id", NpgsqlDbType.Uuid)
        {
            Value = mapObjectId,
        });

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new ObservationRow(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3),
            ReadTimestamp(reader, 4),
            ReadTimestamp(reader, 5),
            reader.IsDBNull(6) ? null : reader.GetFloat(6));
    }

    private async Task<CurrentStatusRow?> ReadCurrentStatusAsync(
        Guid mapObjectId,
        CancellationToken ct)
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
            return null;
        }

        return new CurrentStatusRow(
            reader.IsDBNull(0) ? null : reader.GetGuid(0),
            reader.GetString(1),
            reader.GetInt32(2));
    }

    private async Task<DateTimeOffset?> ReadProcessedAtAsync(Guid messageId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            "select processed_at from outbox_messages where id = @id",
            connection);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = messageId });

        var value = await command.ExecuteScalarAsync(ct);

        return value is DBNull or null
            ? null
            : ReadTimestamp((DateTime)value);
    }

    private Task<long> CountObservationsAsync(Guid mapObjectId, CancellationToken ct) =>
        _api.CountAsync(
            $"select count(*) from observations where map_object_id = '{mapObjectId}'",
            ct);

    private static DateTimeOffset ReadTimestamp(NpgsqlDataReader reader, int ordinal) =>
        ReadTimestamp(reader.GetFieldValue<DateTime>(ordinal));

    private static DateTimeOffset ReadTimestamp(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record Setup(Guid SourceId, Guid StatusId, string StatusCode, Guid ObjectId);

    private sealed record ObservationRow(
        Guid SourceId,
        Guid SourceStatusId,
        string SourceType,
        Guid? RawMessageId,
        DateTimeOffset ObservedAt,
        DateTimeOffset ExpiresAt,
        float? Confidence);

    private sealed record CurrentStatusRow(
        Guid? SourceStatusId,
        string Freshness,
        int ObservationCount);
}
