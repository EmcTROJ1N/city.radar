using System.Net;
using System.Text.Json;

using Api.IntegrationTests.Catalog;
using Api.IntegrationTests.Infrastructure;

using Shouldly;

namespace Api.IntegrationTests.Ingestion;

/// <summary>
/// Адрес из сообщения доходит до <c>observations.address</c>: напрямую через
/// <c>ObservationProposed</c> и через карточку модерации <c>ModerationCaseRequested</c> → approve.
/// Старые payload'ы без поля <c>address</c> дают <c>null</c>.
/// </summary>
[Collection("postgres")]
public sealed class ObservationAddressTests(PostgresFixture fixture) : IAsyncLifetime
{
    private CatalogApi _api = null!;

    public async Task InitializeAsync()
    {
        _api = new CatalogApi(fixture);
        await _api.InitializeAsync();

        await _api.ExecuteAsync("truncate table outbox_messages;");
    }

    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task Proposed_observation_stores_address()
    {
        var ct = CancellationToken.None;
        var setup = await CreateSetupAsync(ct);

        await EnqueueAsync("ObservationProposed", ProposedPayload(setup, address: "Тренёва 3в"), ct);
        (await _api.GetApiDispatcher().DispatchOnceAsync(ct)).ShouldBe(1);

        (await ReadAddressAsync(setup.ObjectId, ct)).ShouldBe("Тренёва 3в");
    }

    [Fact]
    public async Task Proposed_observation_without_address_field_stores_null()
    {
        var ct = CancellationToken.None;
        var setup = await CreateSetupAsync(ct);

        await EnqueueAsync("ObservationProposed", ProposedPayload(setup, address: null, includeAddress: false), ct);
        (await _api.GetApiDispatcher().DispatchOnceAsync(ct)).ShouldBe(1);

        (await _api.CountAsync(
            $"select count(*) from observations where map_object_id = '{setup.ObjectId}' and address is null",
            ct)).ShouldBe(1L);
    }

    [Fact]
    public async Task Approved_case_keeps_address()
    {
        var ct = CancellationToken.None;
        var setup = await CreateSetupAsync(ct);

        var payload = JsonSerializer.Serialize(new
        {
            sourceId = setup.SourceId,
            rawMessageId = setup.RawMessageId,
            kind = "low_confidence",
            reason = "Низкая уверенность LLM",
            proposedStatusCode = "none",
            proposedMapObjectId = setup.ObjectId,
            confidence = 0.4,
            evidenceQuote = "воды нет",
            address = "Тренёва 3в",
        });
        await EnqueueAsync("ModerationCaseRequested", payload, ct);
        (await _api.GetApiDispatcher().DispatchOnceAsync(ct)).ShouldBe(1);

        var caseId = await ReadGuidAsync(
            $"select id from moderation_cases where raw_message_id = '{setup.RawMessageId}'",
            ct);
        var token = await _api.ModeratorTokenAsync(ct);

        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            $"/api/v1/moderation/cases/{caseId}/approve",
            token,
            $$"""{"mapObjectIds":["{{setup.ObjectId}}"]}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await ReadAddressAsync(setup.ObjectId, ct)).ShouldBe("Тренёва 3в");
    }

    private static string ProposedPayload(Setup setup, string? address, bool includeAddress = true) =>
        includeAddress
            ? JsonSerializer.Serialize(new
            {
                sourceId = setup.SourceId,
                mapObjectId = setup.ObjectId,
                statusCode = "none",
                rawMessageId = setup.RawMessageId,
                observedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                confidence = 0.9,
                address,
            })
            : JsonSerializer.Serialize(new
            {
                sourceId = setup.SourceId,
                mapObjectId = setup.ObjectId,
                statusCode = "none",
                rawMessageId = setup.RawMessageId,
                observedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                confidence = 0.9,
            });

    /// <summary>Источник со статусом <c>none</c>, зона категории «Вода» и сообщение чата.</summary>
    private async Task<Setup> CreateSetupAsync(CancellationToken ct)
    {
        var sourceId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var rawMessageId = Guid.NewGuid();

        await _api.ExecuteAsync(
            $"""
             insert into sources (id, name, source_type, settings)
             values ('{sourceId}', 'Адресный чат {sourceId:N}', 'telegram_chat', '{"{"}"chatId": "@addr_{sourceId:N}"{"}"}');
             insert into source_policies (source_id, timeout) values ('{sourceId}', interval '6 hours');
             insert into source_statuses (source_id, code, name, color, icon)
             values ('{sourceId}', 'none', 'Воды нет', 'none', 'cross');
             insert into map_objects (id, object_type_id, name, coverage_kind, geometry)
             values ('{objectId}', '00000000-0000-0000-0000-0000000000b1', 'Зона {objectId:N}', 'polygon',
                     'POLYGON((37.70 48.10, 37.71 48.10, 37.71 48.11, 37.70 48.11, 37.70 48.10))'::geography);
             insert into map_object_sources (map_object_id, source_id) values ('{objectId}', '{sourceId}');
             insert into raw_messages (id, source_id, external_id, text, posted_at)
             values ('{rawMessageId}', '{sourceId}', 'm-{rawMessageId:N}', 'Тренёва 3в, воды нет', now() - interval '5 minutes');
             """,
            ct);

        return new Setup(sourceId, objectId, rawMessageId);
    }

    private Task EnqueueAsync(string type, string payload, CancellationToken ct) =>
        _api.ExecuteAsync(
            $"insert into outbox_messages (destination, type, payload) values ('api', '{type}', '{payload.Replace("'", "''", StringComparison.Ordinal)}'::jsonb)",
            ct);

    private async Task<string?> ReadAddressAsync(Guid objectId, CancellationToken ct)
    {
        await using var connection = new Npgsql.NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new Npgsql.NpgsqlCommand(
            $"select address from observations where map_object_id = '{objectId}'",
            connection);

        return await command.ExecuteScalarAsync(ct) as string;
    }

    private async Task<Guid> ReadGuidAsync(string sql, CancellationToken ct)
    {
        await using var connection = new Npgsql.NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);

        return (Guid)(await command.ExecuteScalarAsync(ct))!;
    }

    private sealed record Setup(Guid SourceId, Guid ObjectId, Guid RawMessageId);
}
