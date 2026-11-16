using System.Net;
using System.Text.Json;

using Api.IntegrationTests.Catalog;
using Api.IntegrationTests.Infrastructure;

using BuildingBlocks.Messaging;
using BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Moderation.Contracts;
using Moderation.Core;

using Npgsql;
using NpgsqlTypes;

using Shouldly;

namespace Api.IntegrationTests.Moderation;

/// <summary>
/// Очередь модерации: список и карточка кейса,
/// фильтры и пагинация, исходное сообщение с <c>evidenceQuote</c>, сокрытие автора, права,
/// идемпотентность outbox-сообщения <c>ModerationCaseRequested</c>, ограничение
/// «complaint ⇔ reporter_id» и участие создателя кейса в транзакции вызывающего.
/// </summary>
/// <remarks>
/// БД общая на коллекцию, поэтому каждый тест начинается с очистки <c>moderation_cases</c> и
/// <c>outbox_messages</c>: иначе очередь по умолчанию (все открытые кейсы) содержала бы кейсы
/// предыдущих тестов.
/// </remarks>
[Collection("postgres")]
public sealed class CasesTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string CasesPath = "/api/v1/moderation/cases";

    private CatalogApi _api = null!;

    public async Task InitializeAsync()
    {
        _api = new CatalogApi(fixture);
        await _api.InitializeAsync();

        await _api.ExecuteAsync("truncate table moderation_cases; truncate table outbox_messages;");
    }

    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task Default_list_returns_only_open_cases()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);

        var openCaseId = await CreateCaseAsync(Case("low_confidence"), ct);
        var approvedCaseId = await CreateCaseAsync(Case("invalid_status"), ct);
        await _api.ExecuteAsync(
            $"update moderation_cases set state = 'approved', resolved_at = now() where id = '{approvedCaseId}'",
            ct);

        using var response = await _api.GetAsync(CasesPath, token, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        var items = json.GetProperty("items").EnumerateArray().ToArray();

        items.ShouldAllBe(item => item.GetProperty("state").GetString() == "open");
        items.Select(item => item.GetProperty("id").GetGuid()).ShouldContain(openCaseId);
        items.Select(item => item.GetProperty("id").GetGuid()).ShouldNotContain(approvedCaseId);
        json.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Filters_by_kind_and_state()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);

        var lowConfidenceId = await CreateCaseAsync(Case("low_confidence"), ct);
        var invalidStatusId = await CreateCaseAsync(Case("invalid_status"), ct);
        var approvedId = await CreateCaseAsync(Case("low_confidence"), ct);
        await _api.ExecuteAsync(
            $"update moderation_cases set state = 'approved', resolved_at = now() where id = '{approvedId}'",
            ct);

        using (var response = await _api.GetAsync($"{CasesPath}?kind=invalid_status", token, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var items = ReadItems(await CatalogApi.ReadJsonAsync(response, ct));
            items.ShouldHaveSingleItem().GetProperty("id").GetGuid().ShouldBe(invalidStatusId);
        }

        using (var response = await _api.GetAsync($"{CasesPath}?kind=low_confidence", token, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var items = ReadItems(await CatalogApi.ReadJsonAsync(response, ct));
            items.ShouldHaveSingleItem().GetProperty("id").GetGuid().ShouldBe(lowConfidenceId);
        }

        using (var response = await _api.GetAsync($"{CasesPath}?state=approved", token, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var items = ReadItems(await CatalogApi.ReadJsonAsync(response, ct));
            items.ShouldHaveSingleItem().GetProperty("id").GetGuid().ShouldBe(approvedId);
        }
    }

    [Fact]
    public async Task Case_detail_includes_raw_message_and_evidence_quote()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var sourceId = await _api.CreateSourceAsync(token, ct: ct);
        var rawMessageId = await InsertRawMessageAsync(
            sourceId,
            "Воды нет на улице Ленина, колонка пустая",
            link: "https://t.me/water/42",
            ct: ct);
        var caseId = await CreateCaseAsync(
            Case(
                "low_confidence",
                sourceId,
                rawMessageId,
                proposed: new
                {
                    statusCode = "none",
                    confidence = 0.42,
                    evidenceQuote = "колонка пустая",
                }),
            ct);

        using var response = await _api.GetAsync($"{CasesPath}/{caseId}", token, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        json.GetProperty("id").GetGuid().ShouldBe(caseId);
        json.GetProperty("kind").GetString().ShouldBe("low_confidence");
        json.GetProperty("state").GetString().ShouldBe("open");
        json.GetProperty("sourceId").GetGuid().ShouldBe(sourceId);

        var rawMessage = json.GetProperty("rawMessage");
        rawMessage.GetProperty("text").GetString()
            .ShouldBe("Воды нет на улице Ленина, колонка пустая");
        rawMessage.GetProperty("link").GetString().ShouldBe("https://t.me/water/42");
        rawMessage.GetProperty("postedAt").ValueKind.ShouldBe(JsonValueKind.String);

        var proposed = json.GetProperty("proposed");
        proposed.GetProperty("statusCode").GetString().ShouldBe("none");
        proposed.GetProperty("confidence").GetDouble().ShouldBe(0.42, 0.0001);
        proposed.GetProperty("evidenceQuote").GetString().ShouldBe("колонка пустая");
    }

    [Fact]
    public async Task Case_detail_hides_message_author()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var sourceId = await _api.CreateSourceAsync(token, ct: ct);
        var author = $"Автор {CatalogApi.NewSuffix()}";
        var rawMessageId = await InsertRawMessageAsync(
            sourceId,
            "Отключили воду на сутки",
            author: author,
            ct: ct);
        var caseId = await CreateCaseAsync(Case("invalid_status", sourceId, rawMessageId), ct);

        using var response = await _api.GetAsync($"{CasesPath}/{caseId}", token, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        body.ShouldNotContain(author);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        var rawMessage = json.GetProperty("rawMessage");
        rawMessage.TryGetProperty("author", out var authorElement).ShouldBeTrue();
        authorElement.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Unknown_case_returns_404()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);

        using var response = await _api.GetAsync($"{CasesPath}/{Guid.NewGuid()}", token, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task User_403()
    {
        var ct = CancellationToken.None;
        var userToken = await _api.UserTokenAsync(ct);
        var caseId = await CreateCaseAsync(Case("low_confidence"), ct);

        using (var response = await _api.GetAsync(CasesPath, userToken, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var response = await _api.GetAsync($"{CasesPath}/{caseId}", userToken, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task Anonymous_401()
    {
        var ct = CancellationToken.None;
        var caseId = await CreateCaseAsync(Case("low_confidence"), ct);

        using (var response = await _api.GetAsync(CasesPath, accessToken: null, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var response = await _api.GetAsync($"{CasesPath}/{caseId}", accessToken: null, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
    }

    [Fact]
    public async Task Duplicate_outbox_delivery_creates_one_case()
    {
        var ct = CancellationToken.None;

        var payload = JsonSerializer.Serialize(new
        {
            sourceId = (Guid?)null,
            rawMessageId = (Guid?)null,
            kind = "low_confidence",
            reason = "Низкая уверенность LLM",
            proposedStatusCode = "none",
            proposedMapObjectId = (Guid?)null,
            confidence = 0.31,
            evidenceQuote = "Воду отключили",
        });
        var messageId = await InsertOutboxAsync("ModerationCaseRequested", payload, ct);

        await using var provider = new ServiceCollection()
            .AddPersistence(fixture.ConnectionString)
            .AddOutbox()
            .AddModeration()
            .AddOutboxDispatcher("api")
            .BuildServiceProvider();

        var dispatcher = provider.GetRequiredService<OutboxDispatcher>();

        (await dispatcher.DispatchOnceAsync(ct)).ShouldBe(1);
        (await CountCasesByOutboxAsync(messageId, ct)).ShouldBe(1L);

        // Повторная доставка того же сообщения (at-least-once): processed_at сброшен.
        await _api.ExecuteAsync(
            $"update outbox_messages set processed_at = null, available_at = now() where id = '{messageId}'",
            ct);

        (await dispatcher.DispatchOnceAsync(ct)).ShouldBe(1);

        (await CountCasesByOutboxAsync(messageId, ct)).ShouldBe(1L);
        (await ReadScalarStringAsync(
            $"select kind || '|' || (proposed ->> 'statusCode') from moderation_cases where outbox_message_id = '{messageId}'",
            ct)).ShouldBe("low_confidence|none");
        (await ReadScalarStringAsync(
            $"select proposed ->> 'evidenceQuote' from moderation_cases where outbox_message_id = '{messageId}'",
            ct)).ShouldBe("Воду отключили");
        (await ReadScalarStringAsync(
            $"select proposed ->> 'reason' from moderation_cases where outbox_message_id = '{messageId}'",
            ct)).ShouldBe("Низкая уверенность LLM");
    }

    [Fact]
    public async Task Complaint_without_reporter_rejected()
    {
        var ct = CancellationToken.None;

        // Валидация создателя: кейс complaint обязан иметь автора жалобы.
        await using (var scope = _api.Factory.Services.CreateAsyncScope())
        {
            var creator = scope.ServiceProvider.GetRequiredService<IModerationCaseCreator>();

            await Should.ThrowAsync<ArgumentException>(() =>
                creator.CreateAsync(Case("complaint"), ct));
        }

        // И ограничение целостности БД на тот же случай.
        var failure = await Should.ThrowAsync<PostgresException>(() =>
            _api.ExecuteAsync("insert into moderation_cases (kind) values ('complaint')", ct));

        failure.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);

        (await CountCasesAsync(ct)).ShouldBe(0L);
    }

    [Fact]
    public async Task Pagination_returns_oldest_first_and_next_cursor()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);

        var oldest = await CreateCaseAsync(Case("low_confidence"), ct);
        var middle = await CreateCaseAsync(Case("low_confidence"), ct);
        var newest = await CreateCaseAsync(Case("low_confidence"), ct);

        // Явные времена создания: проверяем порядок «старые сверху» независимо от часов теста.
        await _api.ExecuteAsync(
            $"""
             update moderation_cases set created_at = now() - interval '3 minutes' where id = '{oldest}';
             update moderation_cases set created_at = now() - interval '2 minutes' where id = '{middle}';
             update moderation_cases set created_at = now() - interval '1 minute' where id = '{newest}';
             """,
            ct);

        using (var response = await _api.GetAsync($"{CasesPath}?limit=2", token, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var json = await CatalogApi.ReadJsonAsync(response, ct);
            var items = ReadItems(json);

            items.Select(item => item.GetProperty("id").GetGuid()).ShouldBe([oldest, middle]);
            json.GetProperty("nextCursor").GetString().ShouldNotBeNullOrWhiteSpace();

            var cursor = json.GetProperty("nextCursor").GetString();

            using var nextResponse = await _api.GetAsync(
                $"{CasesPath}?limit=2&cursor={Uri.EscapeDataString(cursor!)}",
                token,
                ct);

            nextResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

            var nextJson = await CatalogApi.ReadJsonAsync(nextResponse, ct);
            var nextItems = ReadItems(nextJson);

            nextItems.ShouldHaveSingleItem().GetProperty("id").GetGuid().ShouldBe(newest);
            nextJson.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task Creator_joins_caller_transaction_and_rollback_removes_case()
    {
        var ct = CancellationToken.None;

        // Создатель не вызывает SaveChanges: до сохранения вызывающим кейса в БД нет.
        await using (var scope = _api.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MayakDbContext>();
            var creator = scope.ServiceProvider.GetRequiredService<IModerationCaseCreator>();

            await creator.CreateAsync(Case("low_confidence"), ct);

            (await CountCasesAsync(ct)).ShouldBe(0L);

            await db.SaveChangesAsync(ct);

            (await CountCasesAsync(ct)).ShouldBe(1L);

            // Откат транзакции вызывающего убирает и кейс.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await creator.CreateAsync(Case("invalid_status"), ct);
            await db.SaveChangesAsync(ct);
            await transaction.RollbackAsync(ct);
        }

        (await CountCasesAsync(ct)).ShouldBe(1L);
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
        Guid? sourceId = null,
        Guid? rawMessageId = null,
        object? proposed = null,
        Guid? reporterId = null,
        string? comment = null) =>
        new(
            kind,
            sourceId,
            rawMessageId,
            MapObjectId: null,
            ObservationId: null,
            ReporterId: reporterId,
            Comment: comment,
            Proposed: proposed);

    /// <summary>Исходное сообщение источника (таблица принадлежит модулю наблюдений).</summary>
    private async Task<Guid> InsertRawMessageAsync(
        Guid sourceId,
        string text,
        string? author = null,
        string? link = null,
        CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            insert into raw_messages (source_id, external_id, text, author, link, posted_at)
            values (@source_id, @external_id, @text, @author, @link, now())
            returning id
            """,
            connection);
        command.Parameters.AddWithValue("source_id", sourceId);
        command.Parameters.AddWithValue("external_id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("text", text);
        command.Parameters.Add(new NpgsqlParameter("author", NpgsqlDbType.Text)
        {
            Value = (object?)author ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("link", NpgsqlDbType.Text)
        {
            Value = (object?)link ?? DBNull.Value,
        });

        return (Guid)(await command.ExecuteScalarAsync(ct))!;
    }

    private async Task<Guid> InsertOutboxAsync(
        string type,
        string payload,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            insert into outbox_messages (destination, type, payload)
            values ('api', @type, @payload)
            returning id
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("type", NpgsqlDbType.Text) { Value = type });
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payload });

        return (Guid)(await command.ExecuteScalarAsync(ct))!;
    }

    private Task<long> CountCasesAsync(CancellationToken ct) =>
        _api.CountAsync("select count(*) from moderation_cases", ct);

    private Task<long> CountCasesByOutboxAsync(Guid messageId, CancellationToken ct) =>
        _api.CountAsync(
            $"select count(*) from moderation_cases where outbox_message_id = '{messageId}'",
            ct);

    private async Task<string?> ReadScalarStringAsync(string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(sql, connection);

        var result = await command.ExecuteScalarAsync(ct);

        return result is DBNull or null ? null : (string)result;
    }

    private static JsonElement[] ReadItems(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray()];
}
