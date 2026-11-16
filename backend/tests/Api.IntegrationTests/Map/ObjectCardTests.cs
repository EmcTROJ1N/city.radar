using System.Globalization;
using System.Net;
using System.Text.Json;

using Api.IntegrationTests.Catalog;
using Api.IntegrationTests.Infrastructure;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;
using NpgsqlTypes;

using Observations.Application;

using Shouldly;

namespace Api.IntegrationTests.Map;

/// <summary>
/// Карточка фиксации:
/// итоговый статус, состояние по каждому источнику, последние отметки с текстом без автора,
/// ссылка на дисклеймер, 404 для неактивной/несуществующей фиксации и анонимный доступ.
/// Хост Api поднимается на общей БД коллекции; изоляция —
/// уникальные имена и новые фиксации на каждый тест.
/// </summary>
[Collection("postgres")]
public sealed class ObjectCardTests(PostgresFixture fixture) : IAsyncLifetime
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
    public async Task Two_sources_card_shows_state_per_source_and_winner_color()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);

        var (sourceA, statusA) = await CreateSourceAsync(
            token,
            statuses: [("water_no", "Воды нет", "none", "cross")],
            ct);
        var (sourceB, statusB) = await CreateSourceAsync(
            token,
            statuses: [("water_yes", "Вода есть", "ok", "drop")],
            ct);

        var objectId = await CreateObjectAsync(token, [sourceA, sourceB], ct);

        await PostObservationAsync(token, objectId, sourceA, statusA, ct);
        await Task.Delay(30, ct);
        await PostObservationAsync(token, objectId, sourceB, statusB, ct);

        var card = await GetCardAsync(objectId, accessToken: null, ct);

        // /: цвет определяется самым свежим актуальным наблюдением среди обоих.
        var status = card.GetProperty("status");
        status.GetProperty("sourceStatusId").GetGuid().ShouldBe(statusB);
        status.GetProperty("color").GetString().ShouldBe("ok");
        status.GetProperty("freshness").GetString().ShouldBe("fresh");
        status.GetProperty("sourceType").GetString().ShouldBe("moderator");
        status.GetProperty("observationCount").GetInt32().ShouldBe(2);

        // Карточка показывает состояние по каждому источнику.
        var sources = card.GetProperty("sources").EnumerateArray().ToArray();
        sources.Length.ShouldBe(2);

        var cardA = sources.Single(source => source.GetProperty("id").GetGuid() == sourceA);
        var cardB = sources.Single(source => source.GetProperty("id").GetGuid() == sourceB);

        cardA.GetProperty("lastObservation").GetProperty("status").GetProperty("id").GetGuid()
            .ShouldBe(statusA);
        cardB.GetProperty("lastObservation").GetProperty("status").GetProperty("id").GetGuid()
            .ShouldBe(statusB);

        // Итог карточки повторяет поля Feature: геометрия и вид покрытия на месте.
        card.GetProperty("id").GetGuid().ShouldBe(objectId);
        card.GetProperty("coverageKind").GetString().ShouldBe("point");
        card.GetProperty("geometry").GetProperty("type").GetString().ShouldBe("Point");

        //: ссылка на дисклеймер в attributes.
        card.GetProperty("attributes").GetProperty("disclaimerUrl").GetString()
            .ShouldBe("/disclaimer");
    }

    [Fact]
    public async Task Source_without_actual_observation_shown_as_unknown()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);

        var (sourceA, statusA) = await CreateSourceAsync(
            token,
            statuses: [("water_no", "Воды нет", "none", "cross")],
            ct);
        var (sourceB, _) = await CreateSourceAsync(
            token,
            statuses: [("water_yes", "Вода есть", "ok", "drop")],
            ct);

        var objectId = await CreateObjectAsync(token, [sourceA, sourceB], ct);
        await PostObservationAsync(token, objectId, sourceA, statusA, ct);

        var card = await GetCardAsync(objectId, accessToken: null, ct);
        var sources = card.GetProperty("sources").EnumerateArray().ToArray();

        var cardB = sources.Single(source => source.GetProperty("id").GetGuid() == sourceB);
        cardB.GetProperty("lastObservation").ValueKind.ShouldBe(JsonValueKind.Null);
        cardB.GetProperty("observationCount").GetInt32().ShouldBe(0);

        var cardA = sources.Single(source => source.GetProperty("id").GetGuid() == sourceA);
        cardA.GetProperty("lastObservation").ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public async Task Author_never_exposed()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (sourceId, statusId) = await CreateSourceAsync(
            token,
            statuses: [("water_no", "Воды нет", "none", "cross")],
            ct);
        var objectId = await CreateObjectAsync(token, [sourceId], ct);

        const string Author = "Иван Секретов";
        const string Text = "Воды нет с утра, трубу меняют";

        await InsertTelegramObservationAsync(
            objectId,
            sourceId,
            statusId,
            Text,
            Author,
            confidence: 0.9,
            observedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            ct);

        using var response = await _api.GetAsync(
            $"/api/v1/map/objects/{objectId}",
            accessToken: null,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var raw = await response.Content.ReadAsStringAsync(ct);

        raw.ShouldNotContain(Author);
        raw.ShouldNotContain("\"author\"");
        raw.ShouldNotContain("\"reporterId\"");

        using var document = JsonDocument.Parse(raw);
        var card = document.RootElement;
        var observation = card.GetProperty("observations").EnumerateArray().Single();

        observation.GetProperty("text").GetString().ShouldBe(Text);
        observation.GetProperty("sourceType").GetString().ShouldBe("telegram");
    }

    [Fact]
    public async Task Inactive_object_404()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var objectId = await CreateObjectAsync(token, [], ct, isActive: false);

        using var response = await _api.GetAsync(
            $"/api/v1/map/objects/{objectId}",
            accessToken: null,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Unknown_object_404()
    {
        using var response = await _api.GetAsync(
            $"/api/v1/map/objects/{Guid.NewGuid()}",
            accessToken: null,
            CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Retracted_not_shown()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (sourceId, statusId) = await CreateSourceAsync(
            token,
            statuses: [("water_no", "Воды нет", "none", "cross")],
            ct);
        var objectId = await CreateObjectAsync(token, [sourceId], ct);

        var olderId = await InsertTelegramObservationAsync(
            objectId,
            sourceId,
            statusId,
            "Старое сообщение о воде",
            author: "Житель",
            confidence: 0.8,
            observedAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            ct);

        var newerId = await InsertTelegramObservationAsync(
            objectId,
            sourceId,
            statusId,
            "Новое ошибочное сообщение",
            author: "Житель",
            confidence: 0.9,
            observedAt: DateTimeOffset.UtcNow.AddMinutes(-2),
            ct);

        using (var retract = await _api.SendAsync(
            HttpMethod.Post,
            $"/api/v1/observations/{newerId}/retract",
            token,
            """{"reason":"Ошибочное сообщение"}""",
            ct))
        {
            retract.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var card = await GetCardAsync(objectId, accessToken: null, ct);
        var observationIds = card.GetProperty("observations").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .ToArray();

        observationIds.ShouldContain(olderId);
        observationIds.ShouldNotContain(newerId);

        var last = card.GetProperty("sources").EnumerateArray().Single()
            .GetProperty("lastObservation");
        last.GetProperty("id").GetGuid().ShouldBe(olderId);

        card.GetRawText().ShouldNotContain("Новое ошибочное сообщение");
    }

    [Fact]
    public async Task Card_has_source_time_confidence_fields()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (sourceId, statusId) = await CreateSourceAsync(
            token,
            statuses: [("water_no", "Воды нет", "none", "cross")],
            ct: ct,
            timeoutSeconds: 7200);
        var objectId = await CreateObjectAsync(token, [sourceId], ct);

        var observedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        await InsertTelegramObservationAsync(
            objectId,
            sourceId,
            statusId,
            "Воды нет",
            author: "Житель",
            confidence: 0.85,
            observedAt,
            ct);

        // Прямая вставка наблюдения не трогает проекцию: пересчитываем её тем же сервисом.
        await RecalculateAsync(objectId, ct);

        var card = await GetCardAsync(objectId, accessToken: null, ct);

        //: статус карточки несёт источник, время и достоверность.
        var status = card.GetProperty("status");
        status.GetProperty("sourceType").GetString().ShouldBe("telegram");
        status.GetProperty("observedAt").ValueKind.ShouldBe(JsonValueKind.String);
        status.GetProperty("observationCount").GetInt32().ShouldBe(1);
        status.GetProperty("icon").GetString().ShouldBe("cross");
        status.GetProperty("name").GetString().ShouldNotBeNullOrWhiteSpace();
        status.GetProperty("color").GetString().ShouldBe("none");

        var source = card.GetProperty("sources").EnumerateArray().Single();
        source.GetProperty("timeoutSeconds").GetInt32().ShouldBe(7200);
        source.GetProperty("sourceType").GetString().ShouldBe("telegram_chat");
        source.GetProperty("statuses").GetArrayLength().ShouldBe(1);

        var last = source.GetProperty("lastObservation");
        last.GetProperty("confidence").GetDouble().ShouldBe(0.85, 0.0001);
        last.GetProperty("observedAt").ValueKind.ShouldBe(JsonValueKind.String);
        last.GetProperty("expiresAt").ValueKind.ShouldBe(JsonValueKind.String);
        last.GetProperty("sourceType").GetString().ShouldBe("telegram");
        last.GetProperty("sourceName").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Anonymous_can_read()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var (sourceId, statusId) = await CreateSourceAsync(
            token,
            statuses: [("water_yes", "Вода есть", "ok", "drop")],
            ct);
        var objectId = await CreateObjectAsync(token, [sourceId], ct);
        await PostObservationAsync(token, objectId, sourceId, statusId, ct);

        using var response = await _api.GetAsync(
            $"/api/v1/map/objects/{objectId}",
            accessToken: null,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------
    // Данные и запросы
    // ------------------------------------------------------------------

    private async Task<(Guid SourceId, Guid StatusId)> CreateSourceAsync(
        string token,
        IReadOnlyList<(string Code, string Name, string Color, string Icon)> statuses,
        CancellationToken ct,
        int timeoutSeconds = 3600,
        string? name = null)
    {
        var suffix = CatalogApi.NewSuffix();
        name ??= $"Источник {suffix}";

        Guid sourceId;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            SourcesPath,
            token,
            $$"""
              {"name":"{{name}}","sourceType":"telegram_chat",
               "chatId":"@card_{{suffix}}","timeoutSeconds":{{timeoutSeconds.ToString(CultureInfo.InvariantCulture)}},
               "userPrompt":""}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            sourceId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
        }

        Guid firstStatusId = Guid.Empty;

        foreach (var (code, statusName, color, icon) in statuses)
        {
            using var response = await _api.SendAsync(
                HttpMethod.Post,
                $"{SourcesPath}/{sourceId}/statuses",
                token,
                $$"""
                  {"code":"{{code}}","name":"{{statusName}}","description":"Подсказка для LLM",
                   "color":"{{color}}","icon":"{{icon}}"}
                  """,
                ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Created);

            if (firstStatusId == Guid.Empty)
            {
                firstStatusId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
            }
        }

        return (sourceId, firstStatusId);
    }

    private async Task<Guid> CreateObjectAsync(
        string token,
        IReadOnlyList<Guid> sourceIds,
        CancellationToken ct,
        bool isActive = true,
        string? name = null)
    {
        var suffix = CatalogApi.NewSuffix();
        var links = string.Join(",", sourceIds.Select(id => $"\"{id}\""));

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            ObjectsPath,
            token,
            $$"""
              {"name":"{{name ?? $"Фиксация {suffix}"}}","coverageKind":"point",
               "geometry":{"type":"Point","coordinates":[37.80,48.01]},
               "isActive":{{isActive.ToString().ToLowerInvariant()}},"sourceIds":[{{links}}]}
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> PostObservationAsync(
        string token,
        Guid objectId,
        Guid sourceId,
        Guid sourceStatusId,
        CancellationToken ct)
    {
        using var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/observations",
            token,
            $$"""
              {"mapObjectId":"{{objectId}}","sourceId":"{{sourceId}}",
               "sourceStatusId":"{{sourceStatusId}}"}
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await CatalogApi.ReadJsonAsync(response, ct))
            .GetProperty("observation").GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> GetCardAsync(
        Guid objectId,
        string? accessToken,
        CancellationToken ct)
    {
        using var response = await _api.GetAsync(
            $"/api/v1/map/objects/{objectId}",
            accessToken,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CatalogApi.ReadJsonAsync(response, ct);
    }

    /// <summary>Пересчитывает проекцию фиксации после прямой SQL-вставки наблюдения.</summary>
    private async Task RecalculateAsync(Guid objectId, CancellationToken ct)
    {
        await using var scope = _api.Factory.Services.CreateAsyncScope();

        var recalculator = scope.ServiceProvider.GetRequiredService<IStatusRecalculator>();

        await recalculator.RecalculateAsync(objectId, DateTimeOffset.UtcNow, ct);
    }

    /// <summary>Вставляет наблюдение Telegram с текстом и автором в <c>raw_messages</c>.</summary>
    private async Task<Guid> InsertTelegramObservationAsync(
        Guid objectId,
        Guid sourceId,
        Guid sourceStatusId,
        string text,
        string author,
        double confidence,
        DateTimeOffset observedAt,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        var messageId = Guid.NewGuid();

        await using (var message = new NpgsqlCommand(
            """
            insert into raw_messages (id, source_id, external_id, text, author, posted_at)
            values (@id, @source, @external, @text, @author, @posted_at)
            """,
            connection))
        {
            message.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = messageId });
            message.Parameters.Add(new NpgsqlParameter("source", NpgsqlDbType.Uuid) { Value = sourceId });
            message.Parameters.Add(new NpgsqlParameter("external", NpgsqlDbType.Text)
            {
                Value = $"ext-{Guid.NewGuid():N}",
            });
            message.Parameters.Add(new NpgsqlParameter("text", NpgsqlDbType.Text) { Value = text });
            message.Parameters.Add(new NpgsqlParameter("author", NpgsqlDbType.Text) { Value = author });
            message.Parameters.Add(new NpgsqlParameter("posted_at", NpgsqlDbType.TimestampTz)
            {
                Value = observedAt,
            });

            await message.ExecuteNonQueryAsync(ct);
        }

        await using var observation = new NpgsqlCommand(
            """
            insert into observations
              (map_object_id, source_id, source_status_id, source_type, observed_at,
               received_at, expires_at, confidence, raw_message_id)
            values
              (@map_object, @source, @status, 'telegram', @observed_at,
               @observed_at, @expires_at, @confidence, @raw_message)
            returning id
            """,
            connection);
        observation.Parameters.Add(new NpgsqlParameter("map_object", NpgsqlDbType.Uuid)
        {
            Value = objectId,
        });
        observation.Parameters.Add(new NpgsqlParameter("source", NpgsqlDbType.Uuid) { Value = sourceId });
        observation.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Uuid)
        {
            Value = sourceStatusId,
        });
        observation.Parameters.Add(new NpgsqlParameter("observed_at", NpgsqlDbType.TimestampTz)
        {
            Value = observedAt,
        });
        observation.Parameters.Add(new NpgsqlParameter("expires_at", NpgsqlDbType.TimestampTz)
        {
            Value = observedAt.AddHours(1),
        });
        observation.Parameters.Add(new NpgsqlParameter("confidence", NpgsqlDbType.Real)
        {
            Value = (float)confidence,
        });
        observation.Parameters.Add(new NpgsqlParameter("raw_message", NpgsqlDbType.Uuid)
        {
            Value = messageId,
        });

        return (Guid)(await observation.ExecuteScalarAsync(ct))!;
    }
}
