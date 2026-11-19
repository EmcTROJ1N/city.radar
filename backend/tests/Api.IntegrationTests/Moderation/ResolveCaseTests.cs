using System.Globalization;
using System.Net;
using System.Text.Json;

using Api.IntegrationTests.Catalog;
using Api.IntegrationTests.Infrastructure;

using BuildingBlocks.Persistence;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Moderation.Contracts;
using Moderation.Core.Domain;

using Npgsql;
using NpgsqlTypes;

using Shouldly;

namespace Api.IntegrationTests.Moderation;

/// <summary>
/// Принятие и отклонение кейсов модерации
///): выбор фиксации для <c>object_not_found</c>, правка статуса, правила 409/422,
/// наблюдение с временем сообщения, аудит и права.
/// </summary>
/// <remarks>
/// БД общая на коллекцию; изоляция — новые источники, статусы, фиксации и сообщения на каждый
/// тест. Идентификаторы кейсов уникальны, поэтому очередь соседних тестов не мешает.
/// </remarks>
[Collection("postgres")]
public sealed class ResolveCaseTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string CasesPath = "/api/v1/moderation/cases";
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
    public async Task Approve_object_not_found_creates_observation_on_map()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var source = await CreateSourceAsync(token, ct, linkByText: true);
        var objectId = await CreateObjectAsync(token, [source.SourceId], ct);
        var rawMessageId = await InsertRawMessageAsync(
            source.SourceId,
            "Воду дали на Ленинском",
            DateTimeOffset.UtcNow.AddMinutes(-10),
            ct);
        var caseId = await CreateCaseAsync(
            Case(
                "object_not_found",
                source.SourceId,
                rawMessageId,
                ObjectId: null,
                new { statusCode = source.StatusA.Code, confidence = 0.91 }),
            ct);

        using var response = await ApproveAsync(token, caseId, [objectId], ct: ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        json.GetProperty("id").GetGuid().ShouldBe(caseId);
        json.GetProperty("state").GetString().ShouldBe("approved");
        json.GetProperty("mapObjectId").GetGuid().ShouldBe(objectId);
        json.GetProperty("resolvedAt").ValueKind.ShouldBe(JsonValueKind.String);

        (await _api.CountAsync(
            $"""
             select count(*) from observations
             where map_object_id = '{objectId}'
               and source_id = '{source.SourceId}'
               and source_status_id = '{source.StatusA.Id}'
               and raw_message_id = '{rawMessageId}'
               and source_type = 'telegram'
             """,
            ct)).ShouldBe(1L);

        //: наблюдение сразу попадает в проекцию, карта читает current_statuses.
        (await _api.CountAsync(
            $"""
             select count(*) from current_statuses
             where map_object_id = '{objectId}'
               and source_status_id = '{source.StatusA.Id}'
               and observation_count = 1
             """,
            ct)).ShouldBe(1L);
    }

    [Fact]
    public async Task Approve_with_status_correction_uses_requested_status()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var source = await CreateSourceAsync(token, ct, linkByText: false);
        var objectId = await CreateObjectAsync(token, [source.SourceId], ct);
        var rawMessageId = await InsertRawMessageAsync(
            source.SourceId,
            "Воды нет, но напор слабый",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            ct);
        var caseId = await CreateCaseAsync(
            Case(
                "low_confidence",
                source.SourceId,
                rawMessageId,
                ObjectId: objectId,
                new { statusCode = source.StatusA.Code, confidence = 0.4 }),
            ct);

        using var response = await ApproveAsync(
            token,
            caseId,
            mapObjectIds: [],
            sourceStatusId: source.StatusB.Id,
            ct: ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await _api.CountAsync(
            $"""
             select count(*) from observations
             where map_object_id = '{objectId}'
               and source_status_id = '{source.StatusB.Id}'
             """,
            ct)).ShouldBe(1L);

        (await _api.CountAsync(
            $"""
             select count(*) from current_statuses
             where map_object_id = '{objectId}'
               and source_status_id = '{source.StatusB.Id}'
             """,
            ct)).ShouldBe(1L);
    }

    [Fact]
    public async Task Object_outside_source_list_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var source = await CreateSourceAsync(token, ct, linkByText: true);
        var otherSource = await CreateSourceAsync(token, ct, linkByText: true);
        var outsideObjectId = await CreateObjectAsync(token, [otherSource.SourceId], ct);
        var rawMessageId = await InsertRawMessageAsync(
            source.SourceId,
            "Воду дали у школы",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            ct);
        var caseId = await CreateCaseAsync(
            Case(
                "object_not_found",
                source.SourceId,
                rawMessageId,
                ObjectId: null,
                new { statusCode = source.StatusA.Code }),
            ct);

        using var response = await ApproveAsync(token, caseId, [outsideObjectId], ct: ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "mapObjectIds");
        (await CountObservationsAsync(outsideObjectId, ct)).ShouldBe(0L);
    }

    [Fact]
    public async Task Status_outside_source_list_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var source = await CreateSourceAsync(token, ct, linkByText: false);
        var otherSource = await CreateSourceAsync(token, ct, linkByText: true);
        var objectId = await CreateObjectAsync(token, [source.SourceId], ct);
        var rawMessageId = await InsertRawMessageAsync(
            source.SourceId,
            "Воды нет",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            ct);
        var caseId = await CreateCaseAsync(
            Case(
                "low_confidence",
                source.SourceId,
                rawMessageId,
                ObjectId: objectId,
                new { statusCode = source.StatusA.Code }),
            ct);

        using var response = await ApproveAsync(
            token,
            caseId,
            mapObjectIds: [],
            sourceStatusId: otherSource.StatusA.Id,
            ct: ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "sourceStatusId");
        (await CountObservationsAsync(objectId, ct)).ShouldBe(0L);
    }

    [Fact]
    public async Task Repeated_decision_409()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var source = await CreateSourceAsync(token, ct, linkByText: false);
        var objectId = await CreateObjectAsync(token, [source.SourceId], ct);
        var rawMessageId = await InsertRawMessageAsync(
            source.SourceId,
            "Вода есть",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            ct);
        var caseId = await CreateCaseAsync(
            Case(
                "low_confidence",
                source.SourceId,
                rawMessageId,
                ObjectId: objectId,
                new { statusCode = source.StatusA.Code }),
            ct);

        using (var first = await ApproveAsync(token, caseId, [objectId], ct: ct))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var second = await ApproveAsync(token, caseId, [objectId], ct: ct))
        {
            second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        (await CountObservationsAsync(objectId, ct)).ShouldBe(1L);
    }

    [Fact]
    public async Task Approve_is_audited()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var source = await CreateSourceAsync(token, ct, linkByText: false);
        var objectId = await CreateObjectAsync(token, [source.SourceId], ct);
        var rawMessageId = await InsertRawMessageAsync(
            source.SourceId,
            "Вода есть",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            ct);
        var caseId = await CreateCaseAsync(
            Case(
                "low_confidence",
                source.SourceId,
                rawMessageId,
                ObjectId: objectId,
                new { statusCode = source.StatusA.Code }),
            ct);

        using (var response = await ApproveAsync(token, caseId, [objectId], ct: ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var entry = await _api.FindAuditAsync(
            "moderation.case.approve",
            caseId.ToString(),
            ct);

        entry.ShouldNotBeNull("Одобрение кейса должно попасть в аудит.");
        entry!.ActorId.ShouldBe(CatalogApi.ModeratorId);
        entry.EntityType.ShouldBe("moderation_case");
        entry.Before.ShouldNotBeNull();
        entry.After.ShouldNotBeNull();
        entry.After!.Value.GetProperty("state").GetString().ShouldBe("approved");
    }

    [Fact]
    public async Task Reject_creates_no_observation()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var source = await CreateSourceAsync(token, ct, linkByText: false);
        var objectId = await CreateObjectAsync(token, [source.SourceId], ct);
        var rawMessageId = await InsertRawMessageAsync(
            source.SourceId,
            "Слух о воде",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            ct);
        var caseId = await CreateCaseAsync(
            Case(
                "low_confidence",
                source.SourceId,
                rawMessageId,
                ObjectId: objectId,
                new { statusCode = source.StatusA.Code }),
            ct);

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            $"{CasesPath}/{caseId}/reject",
            token,
            """{"reason":"Сообщение не подтверждается"}""",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        json.GetProperty("state").GetString().ShouldBe("rejected");
        json.GetProperty("resolvedAt").ValueKind.ShouldBe(JsonValueKind.String);

        (await CountObservationsAsync(objectId, ct)).ShouldBe(0L);
        (await _api.CountAsync(
            $"select count(*) from current_statuses where map_object_id = '{objectId}'",
            ct)).ShouldBe(0L);

        var entry = await _api.FindAuditAsync(
            "moderation.case.reject",
            caseId.ToString(),
            ct);
        entry.ShouldNotBeNull("Отклонение кейса должно попасть в аудит.");
        entry!.ActorId.ShouldBe(CatalogApi.ModeratorId);
    }

    [Fact]
    public async Task Approve_object_not_found_requires_map_object_ids_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var source = await CreateSourceAsync(token, ct, linkByText: true);
        var rawMessageId = await InsertRawMessageAsync(
            source.SourceId,
            "Воду дали",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            ct);
        var caseId = await CreateCaseAsync(
            Case(
                "object_not_found",
                source.SourceId,
                rawMessageId,
                ObjectId: null,
                new { statusCode = source.StatusA.Code }),
            ct);

        using var response = await ApproveAsync(token, caseId, [], ct: ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "mapObjectIds");
    }

    [Fact]
    public async Task Approve_uses_posted_at_not_now()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var source = await CreateSourceAsync(token, ct, linkByText: true, timeoutSeconds: 3600);
        var objectId = await CreateObjectAsync(token, [source.SourceId], ct);
        var postedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var rawMessageId = await InsertRawMessageAsync(
            source.SourceId,
            "Воду давали два часа назад",
            postedAt,
            ct);
        var caseId = await CreateCaseAsync(
            Case(
                "object_not_found",
                source.SourceId,
                rawMessageId,
                ObjectId: null,
                new { statusCode = source.StatusA.Code }),
            ct);

        using var response = await ApproveAsync(token, caseId, [objectId], ct: ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var row = await ReadObservationAsync(objectId, ct);
        row.ShouldNotBeNull();
        row!.ObservedAt.ShouldBeInRange(postedAt.AddSeconds(-1), postedAt.AddSeconds(1));
        row.ExpiresAt.ShouldBeInRange(postedAt.AddHours(1).AddSeconds(-1), postedAt.AddHours(1).AddSeconds(1));
        row.ExpiresAt.ShouldBeLessThan(DateTimeOffset.UtcNow);

        // Протухшее сообщение сохраняется для истории, но статус не выставляет.
        var status = await ReadCurrentStatusAsync(objectId, ct);
        status.ShouldNotBeNull();
        status!.SourceStatusId.ShouldBeNull();
        status.Freshness.ShouldBe("stale");
        status.ObservationCount.ShouldBe(0);
    }

    [Fact]
    public async Task Approve_anomaly_marks_processed_without_observation()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var caseId = await CreateCaseAsync(
            Case(
                "anomaly",
                SourceId: null,
                RawMessageId: null,
                ObjectId: null,
                new { count = 12, windowSeconds = 300 }),
            ct);

        var observationsBefore = await _api.CountAsync("select count(*) from observations", ct);

        using var response = await ApproveAsync(token, caseId, [], ct: ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        json.GetProperty("state").GetString().ShouldBe("approved");

        (await _api.CountAsync("select count(*) from observations", ct))
            .ShouldBe(observationsBefore);
        (await _api.FindAuditAsync("moderation.case.approve", caseId.ToString(), ct))
            .ShouldNotBeNull();
    }

    [Fact]
    public async Task User_403_Anonymous_401()
    {
        var ct = CancellationToken.None;
        var userToken = await _api.UserTokenAsync(ct);
        var source = await CreateSourceAsync(
            await _api.ModeratorTokenAsync(ct),
            ct,
            linkByText: false);
        var rawMessageId = await InsertRawMessageAsync(
            source.SourceId,
            "Вода есть",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            ct);
        var caseId = await CreateCaseAsync(
            Case(
                "low_confidence",
                source.SourceId,
                rawMessageId,
                ObjectId: null,
                new { statusCode = source.StatusA.Code }),
            ct);

        using (var anonymous = await ApproveAsync(
            accessToken: null,
            caseId,
            [],
            ct: ct))
        {
            anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var user = await ApproveAsync(userToken, caseId, [], ct: ct))
        {
            user.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var anonymousReject = await _api.SendAsync(
            HttpMethod.Post,
            $"{CasesPath}/{caseId}/reject",
            accessToken: null,
            """{"reason":"нет"}""",
            ct))
        {
            anonymousReject.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var userReject = await _api.SendAsync(
            HttpMethod.Post,
            $"{CasesPath}/{caseId}/reject",
            userToken,
            """{"reason":"нет"}""",
            ct))
        {
            userReject.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task Approve_all_objects_of_source_when_link_by_text_off()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var source = await CreateSourceAsync(token, ct, linkByText: false);
        var firstObjectId = await CreateObjectAsync(token, [source.SourceId], ct);
        var secondObjectId = await CreateObjectAsync(token, [source.SourceId], ct);
        var rawMessageId = await InsertRawMessageAsync(
            source.SourceId,
            "По всему району вода есть",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            ct);
        var caseId = await CreateCaseAsync(
            Case(
                "low_confidence",
                source.SourceId,
                rawMessageId,
                ObjectId: null,
                new { statusCode = source.StatusA.Code, confidence = 0.35 }),
            ct);

        using var response = await ApproveAsync(token, caseId, [], ct: ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await CountObservationsAsync(firstObjectId, ct)).ShouldBe(1L);
        (await CountObservationsAsync(secondObjectId, ct)).ShouldBe(1L);

        (await _api.CountAsync(
            $"""
             select count(*) from current_statuses
             where map_object_id in ('{firstObjectId}', '{secondObjectId}')
               and source_status_id = '{source.StatusA.Id}'
             """,
            ct)).ShouldBe(2L);
    }

    // ------------------------------------------------------------------
    // Данные, запросы и проверки
    // ------------------------------------------------------------------

    private Task<HttpResponseMessage> ApproveAsync(
        string? accessToken,
        Guid caseId,
        IReadOnlyList<Guid> mapObjectIds,
        Guid? sourceStatusId = null,
        CancellationToken ct = default)
    {
        var body = ApproveBody(mapObjectIds, sourceStatusId);

        return _api.SendAsync(
            HttpMethod.Post,
            $"{CasesPath}/{caseId}/approve",
            accessToken,
            body,
            ct);
    }

    private static string ApproveBody(
        IReadOnlyList<Guid> mapObjectIds,
        Guid? sourceStatusId)
    {
        var ids = string.Join(
            ",",
            mapObjectIds.Select(id => $"\"{id}\""));
        var statusPart = sourceStatusId is { } statusId
            ? $",\"sourceStatusId\":\"{statusId}\""
            : string.Empty;

        return $$"""{"mapObjectIds":[{{ids}}]{{statusPart}}}""";
    }

    private async Task<SourceSetup> CreateSourceAsync(
        string token,
        CancellationToken ct,
        bool linkByText,
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
               "chatId":"@mod_{{suffix}}","timeoutSeconds":{{timeoutSeconds.ToString(CultureInfo.InvariantCulture)}},
               "userPrompt":"","linkByText":{{linkByText.ToString().ToLowerInvariant()}}}
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

    private async Task<StatusSetup> CreateStatusAsync(
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
              {"code":"status_{{suffix}}","name":"{{name}}","description":"Статус источника",
               "color":"ok","icon":"drop"}
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var json = await CatalogApi.ReadJsonAsync(response, ct);

        return new StatusSetup(
            json.GetProperty("id").GetGuid(),
            json.GetProperty("code").GetString()!);
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
              {"name":"Фиксация {{suffix}}","objectTypeId":"00000000-0000-0000-0000-00000000aa01","coverageKind":"point",
               "geometry":{"type":"Point","coordinates":[37.80,48.01]},
               "sourceIds":[{{links}}]}
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateCaseAsync(NewModerationCase input, CancellationToken ct)
    {
        await using var scope = _api.Factory.Services.CreateAsyncScope();

        var id = await scope.ServiceProvider
            .GetRequiredService<IModerationCaseCreator>()
            .CreateAsync(input, ct);

        await scope.ServiceProvider.GetRequiredService<MayakDbContext>().SaveChangesAsync(ct);

        return id;
    }

    private static NewModerationCase Case(
        string kind,
        Guid? SourceId,
        Guid? RawMessageId,
        Guid? ObjectId,
        object? proposed) =>
        new(
            kind,
            SourceId,
            RawMessageId,
            MapObjectId: ObjectId,
            ObservationId: null,
            ReporterId: null,
            Comment: null,
            Proposed: proposed);

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

    private Task<long> CountObservationsAsync(Guid mapObjectId, CancellationToken ct) =>
        _api.CountAsync(
            $"select count(*) from observations where map_object_id = '{mapObjectId}'",
            ct);

    private async Task<ObservationRow?> ReadObservationAsync(
        Guid mapObjectId,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select observed_at, expires_at
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
            ReadTimestamp(reader, 0),
            ReadTimestamp(reader, 1));
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

    private static DateTimeOffset ReadTimestamp(NpgsqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetFieldValue<DateTime>(ordinal), DateTimeKind.Utc));

    private sealed record SourceSetup(Guid SourceId, StatusSetup StatusA, StatusSetup StatusB);

    private sealed record StatusSetup(Guid Id, string Code);

    private sealed record ObservationRow(DateTimeOffset ObservedAt, DateTimeOffset ExpiresAt);

    private sealed record CurrentStatusRow(
        Guid? SourceStatusId,
        string Freshness,
        int ObservationCount);
}
