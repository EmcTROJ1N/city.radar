using System.Globalization;
using System.Text.Json;

using Api.IntegrationTests.Catalog;
using Api.IntegrationTests.Infrastructure;

using Microsoft.Extensions.DependencyInjection;

using Moderation.Core.Anomaly;

using Npgsql;
using NpgsqlTypes;

using Shouldly;

namespace Api.IntegrationTests.Moderation;

/// <summary>
/// Детектор аномалий: всплеск пользовательских
/// отметок по району за окно выше порога создаёт один кейс <c>anomaly</c> и не дублирует его,
/// пока кейс открыт. Порог и окно берутся из <c>global_settings</c> на каждой проверке.
/// </summary>
/// <remarks>
/// «Район» — группа фиксации (у тестовых объектов группы уникальны). Наблюдения вставляются
/// напрямую в БД с управляемым <c>received_at</c>: так проверяются окно, тип источника и
/// отсутствие влияния старых отметок. Детектор вызывается напрямую с явным <c>now</c> либо
/// через <see cref="AnomalyHandler"/> — ровно так его вызывает диспетчер outbox.
/// </remarks>
[Collection("postgres")]
public sealed class AnomalyTests(PostgresFixture fixture) : IAsyncLifetime
{
    private CatalogApi _api = null!;

    public async Task InitializeAsync()
    {
        _api = new CatalogApi(fixture);
        await _api.InitializeAsync();
    }

    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task Burst_over_threshold_creates_single_anomaly_case()
    {
        var ct = CancellationToken.None;
        var now = DateTimeOffset.UtcNow;
        await SetSettingsAsync(threshold: 2, windowMinutes: 5, ct);
        var setup = await CreateSetupAsync(ct);

        for (var index = 0; index < 3; index++)
        {
            await InsertObservationAsync(setup, "manual", now, ct);
        }

        var caseId = await RunDetectorAsync(setup.GroupId, setup.ObjectId, now, ct);

        caseId.ShouldNotBeNull();

        var proposed = await ReadProposedAsync(caseId!.Value, ct);
        proposed.ShouldNotBeNull();
        using (var document = JsonDocument.Parse(proposed!))
        {
            var root = document.RootElement;
            root.GetProperty("count").GetInt64().ShouldBe(3);
            root.GetProperty("windowSeconds").GetInt32().ShouldBe(300);
            root.GetProperty("groupId").GetGuid().ShouldBe(setup.GroupId);
        }

        (await CountCasesAsync(setup.GroupId, ct)).ShouldBe(1L);

        // Повторное событие при открытом кейсе второй кейс не создаёт.
        await InsertObservationAsync(setup, "manual", now, ct);
        (await RunDetectorAsync(setup.GroupId, setup.ObjectId, now, ct)).ShouldBeNull();
        (await CountCasesAsync(setup.GroupId, ct)).ShouldBe(1L);
    }

    [Fact]
    public async Task Below_threshold_no_case()
    {
        var ct = CancellationToken.None;
        var now = DateTimeOffset.UtcNow;
        await SetSettingsAsync(threshold: 5, windowMinutes: 5, ct);
        var setup = await CreateSetupAsync(ct);

        for (var index = 0; index < 3; index++)
        {
            await InsertObservationAsync(setup, "manual", now, ct);
        }

        (await RunDetectorAsync(setup.GroupId, setup.ObjectId, now, ct)).ShouldBeNull();
        (await CountCasesAsync(setup.GroupId, ct)).ShouldBe(0L);
    }

    [Fact]
    public async Task Open_case_suppresses_duplicates()
    {
        var ct = CancellationToken.None;
        var now = DateTimeOffset.UtcNow;
        await SetSettingsAsync(threshold: 2, windowMinutes: 5, ct);
        var setup = await CreateSetupAsync(ct);

        for (var index = 0; index < 3; index++)
        {
            await InsertObservationAsync(setup, "manual", now, ct);
        }

        (await RunDetectorAsync(setup.GroupId, setup.ObjectId, now, ct)).ShouldNotBeNull();

        for (var index = 0; index < 5; index++)
        {
            await InsertObservationAsync(setup, "manual", now, ct);
        }

        (await RunDetectorAsync(setup.GroupId, setup.ObjectId, now, ct)).ShouldBeNull();
        (await CountCasesAsync(setup.GroupId, ct)).ShouldBe(1L);
        (await CountOpenCasesAsync(setup.GroupId, ct)).ShouldBe(1L);
    }

    [Fact]
    public async Task After_resolve_new_burst_creates_new_case()
    {
        var ct = CancellationToken.None;
        var now = DateTimeOffset.UtcNow;
        await SetSettingsAsync(threshold: 2, windowMinutes: 5, ct);
        var setup = await CreateSetupAsync(ct);

        for (var index = 0; index < 3; index++)
        {
            await InsertObservationAsync(setup, "manual", now, ct);
        }

        var firstCaseId = await RunDetectorAsync(setup.GroupId, setup.ObjectId, now, ct);
        firstCaseId.ShouldNotBeNull();

        await _api.ExecuteAsync(
            $"update moderation_cases set state = 'rejected', resolved_at = now(), "
            + $"resolved_by = '{CatalogApi.ModeratorId}' where id = '{firstCaseId}'",
            ct);

        var secondCaseId = await RunDetectorAsync(setup.GroupId, setup.ObjectId, now, ct);

        secondCaseId.ShouldNotBeNull();
        secondCaseId.ShouldNotBe(firstCaseId);
        (await CountCasesAsync(setup.GroupId, ct)).ShouldBe(2L);
        (await CountOpenCasesAsync(setup.GroupId, ct)).ShouldBe(1L);
    }

    [Fact]
    public async Task Threshold_follows_global_settings()
    {
        var ct = CancellationToken.None;
        var now = DateTimeOffset.UtcNow;
        await SetSettingsAsync(threshold: 5, windowMinutes: 5, ct);
        var setup = await CreateSetupAsync(ct);

        for (var index = 0; index < 3; index++)
        {
            await InsertObservationAsync(setup, "manual", now, ct);
        }

        (await RunDetectorAsync(setup.GroupId, setup.ObjectId, now, ct)).ShouldBeNull();

        // Настройка читается на каждой проверке: перезапуск не нужен.
        await SetSettingsAsync(threshold: 2, windowMinutes: 5, ct);

        (await RunDetectorAsync(setup.GroupId, setup.ObjectId, now, ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Window_follows_global_settings()
    {
        var ct = CancellationToken.None;
        var now = DateTimeOffset.UtcNow;
        await SetSettingsAsync(threshold: 2, windowMinutes: 1, ct);
        var setup = await CreateSetupAsync(ct);

        // Отметки в 10 минутах от now при окне 1 минута не считаются.
        for (var index = 0; index < 3; index++)
        {
            await InsertObservationAsync(setup, "manual", now.AddMinutes(-10), ct);
        }

        (await RunDetectorAsync(setup.GroupId, setup.ObjectId, now, ct)).ShouldBeNull();

        await SetSettingsAsync(threshold: 2, windowMinutes: 60, ct);

        var caseId = await RunDetectorAsync(setup.GroupId, setup.ObjectId, now, ct);

        caseId.ShouldNotBeNull();
        var proposed = await ReadProposedAsync(caseId!.Value, ct);
        proposed.ShouldNotBeNull();
        using var document = JsonDocument.Parse(proposed!);
        document.RootElement.GetProperty("windowSeconds").GetInt32().ShouldBe(3600);
    }

    [Fact]
    public async Task Moderator_observations_not_counted()
    {
        var ct = CancellationToken.None;
        var now = DateTimeOffset.UtcNow;
        await SetSettingsAsync(threshold: 2, windowMinutes: 5, ct);
        var setup = await CreateSetupAsync(ct);

        for (var index = 0; index < 3; index++)
        {
            await InsertObservationAsync(setup, "moderator", now, ct);
        }

        (await RunDetectorAsync(setup.GroupId, setup.ObjectId, now, ct)).ShouldBeNull();
        (await CountCasesAsync(setup.GroupId, ct)).ShouldBe(0L);
    }

    [Fact]
    public async Task Different_groups_independent()
    {
        var ct = CancellationToken.None;
        var now = DateTimeOffset.UtcNow;
        await SetSettingsAsync(threshold: 2, windowMinutes: 5, ct);
        var first = await CreateSetupAsync(ct);
        var second = await CreateSetupAsync(ct);

        // Первая группа: ровно порог — всплеска нет.
        await InsertObservationAsync(first, "manual", now, ct);
        await InsertObservationAsync(first, "manual", now, ct);

        // Вторая группа: больше порога — кейс только по ней.
        for (var index = 0; index < 3; index++)
        {
            await InsertObservationAsync(second, "manual", now, ct);
        }

        (await RunDetectorAsync(first.GroupId, first.ObjectId, now, ct)).ShouldBeNull();
        (await RunDetectorAsync(second.GroupId, second.ObjectId, now, ct)).ShouldNotBeNull();

        (await CountCasesAsync(first.GroupId, ct)).ShouldBe(0L);
        (await CountCasesAsync(second.GroupId, ct)).ShouldBe(1L);

        // Ещё одна отметка в первой группе превышает порог — независимый кейс.
        await InsertObservationAsync(first, "manual", now, ct);

        (await RunDetectorAsync(first.GroupId, first.ObjectId, now, ct)).ShouldNotBeNull();
        (await CountCasesAsync(first.GroupId, ct)).ShouldBe(1L);
        (await CountCasesAsync(second.GroupId, ct)).ShouldBe(1L);
    }

    [Fact]
    public async Task Old_observations_outside_window_not_counted()
    {
        var ct = CancellationToken.None;
        var now = DateTimeOffset.UtcNow;
        await SetSettingsAsync(threshold: 2, windowMinutes: 5, ct);
        var setup = await CreateSetupAsync(ct);

        // Старые отметки: за окном, в кейс попасть не должны.
        for (var index = 0; index < 3; index++)
        {
            await InsertObservationAsync(setup, "manual", now.AddMinutes(-10), ct);
        }

        (await RunDetectorAsync(setup.GroupId, setup.ObjectId, now, ct)).ShouldBeNull();

        // Три свежие отметки дают кейс ровно на 3, а не на 6.
        for (var index = 0; index < 3; index++)
        {
            await InsertObservationAsync(setup, "manual", now, ct);
        }

        var caseId = await RunDetectorAsync(setup.GroupId, setup.ObjectId, now, ct);

        caseId.ShouldNotBeNull();
        var proposed = await ReadProposedAsync(caseId!.Value, ct);
        proposed.ShouldNotBeNull();
        using var document = JsonDocument.Parse(proposed!);
        document.RootElement.GetProperty("count").GetInt64().ShouldBe(3);
    }

    [Fact]
    public async Task Category_burst_without_group_creates_case()
    {
        var ct = CancellationToken.None;
        var now = DateTimeOffset.UtcNow;
        await SetSettingsAsync(threshold: 2, windowMinutes: 5, ct);
        var setup = await CreateSetupAsync(ct, withGroup: false, withCategory: true);

        for (var index = 0; index < 3; index++)
        {
            await InsertObservationAsync(setup, "manual", now, ct);
        }

        // Группы нет — районом считается категория типа объекта.
        var caseId = await RunDetectorAsync(groupId: null, setup.ObjectId, now, ct);

        caseId.ShouldNotBeNull();

        var proposed = await ReadProposedAsync(caseId!.Value, ct);
        proposed.ShouldNotBeNull();
        using var document = JsonDocument.Parse(proposed!);
        var root = document.RootElement;
        root.GetProperty("count").GetInt64().ShouldBe(3);
        root.GetProperty("categoryId").GetGuid().ShouldBe(setup.CategoryId!.Value);
        root.GetProperty("groupId").ValueKind.ShouldBe(JsonValueKind.Null);

        (await CountCasesByCategoryAsync(setup.CategoryId!.Value, ct)).ShouldBe(1L);
    }

    [Fact]
    public async Task Parallel_events_create_one_case()
    {
        var ct = CancellationToken.None;
        var now = DateTimeOffset.UtcNow;
        await SetSettingsAsync(threshold: 2, windowMinutes: 5, ct);
        var setup = await CreateSetupAsync(ct);

        for (var index = 0; index < 3; index++)
        {
            await InsertObservationAsync(setup, "manual", now, ct);
        }

        var payload = JsonSerializer.Serialize(new
        {
            observationId = Guid.NewGuid(),
            mapObjectId = setup.ObjectId,
            groupId = setup.GroupId,
            sourceKind = "manual",
            at = now,
        });

        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var scope = _api.Factory.Services.CreateAsyncScope();
            var handler = scope.ServiceProvider
                .GetServices<BuildingBlocks.Messaging.IOutboxHandler>()
                .Single(candidate => candidate.Type == AnomalyHandler.MessageType);

            using var document = JsonDocument.Parse(payload);
            await handler.HandleAsync(
                document.RootElement.Clone(),
                Guid.NewGuid(),
                ct);
        });

        await Task.WhenAll(tasks);

        (await CountCasesAsync(setup.GroupId, ct)).ShouldBe(1L);
        (await CountOpenCasesAsync(setup.GroupId, ct)).ShouldBe(1L);
    }

    // ------------------------------------------------------------------
    // Данные и запросы
    // ------------------------------------------------------------------

    private async Task SetSettingsAsync(int threshold, int windowMinutes, CancellationToken ct)
    {
        await _api.ExecuteAsync(
            $"""
             insert into global_settings (key, value) values
               ('anomaly_threshold', '{threshold.ToString(CultureInfo.InvariantCulture)}'::jsonb),
               ('anomaly_window_minutes', '{windowMinutes.ToString(CultureInfo.InvariantCulture)}'::jsonb)
             on conflict (key) do update set value = excluded.value, updated_at = now();
             """,
            ct);
    }

    private async Task<Setup> CreateSetupAsync(
        CancellationToken ct,
        bool withGroup = true,
        bool withCategory = false)
    {
        var token = await _api.ModeratorTokenAsync(ct);
        var suffix = CatalogApi.NewSuffix();
        var sourceId = await _api.CreateSourceAsync(token, ct: ct);

        Guid statusId;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            $"/api/v1/sources/{sourceId}/statuses",
            token,
            $$"""
              {"code":"water_{{suffix}}","name":"Вода есть {{suffix}}","description":"Подача идёт",
               "color":"ok","icon":"drop"}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);
            statusId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
        }

        var groupId = Guid.Empty;
        if (withGroup)
        {
            using var groupResponse = await _api.SendAsync(
                HttpMethod.Post,
                "/api/v1/catalog/groups",
                token,
                $$"""{"name":"Район {{suffix}}"}""",
                ct);
            groupResponse.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);
            groupId = (await CatalogApi.ReadJsonAsync(groupResponse, ct)).GetProperty("id").GetGuid();
        }

        Guid? categoryId = null;
        Guid? objectTypeId = null;
        if (withCategory)
        {
            categoryId = await _api.CreateCategoryAsync(token, ct: ct);

            using var typeResponse = await _api.SendAsync(
                HttpMethod.Post,
                "/api/v1/catalog/object-types",
                token,
                $$"""
                  {"categoryId":"{{categoryId}}","code":"type_{{suffix}}","name":"Тип {{suffix}}",
                   "attributesSchema":{},"geometryKind":"point"}
                  """,
                ct);
            typeResponse.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);
            objectTypeId = (await CatalogApi.ReadJsonAsync(typeResponse, ct)).GetProperty("id").GetGuid();
        }

        var scope = withGroup
            ? $"\"groupId\":\"{groupId}\","
            : withCategory
                ? $"\"objectTypeId\":\"{objectTypeId}\","
                : string.Empty;

        Guid objectId;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/objects",
            token,
            $$"""
              {"name":"Фиксация {{suffix}}",{{scope}}"coverageKind":"point",
               "geometry":{"type":"Point","coordinates":[37.80,48.01]},"sourceIds":["{{sourceId}}"]}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);
            objectId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
        }

        return new Setup(groupId, objectId, sourceId, statusId, categoryId);
    }

    private async Task InsertObservationAsync(
        Setup setup,
        string sourceType,
        DateTimeOffset receivedAt,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            insert into observations
              (map_object_id, source_id, source_status_id, source_type, reporter_id,
               observed_at, received_at, expires_at)
            values
              (@map_object, @source, @status, @source_type, @reporter,
               @observed_at, @received_at, @expires_at)
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("map_object", NpgsqlDbType.Uuid)
        {
            Value = setup.ObjectId,
        });
        command.Parameters.Add(new NpgsqlParameter("source", NpgsqlDbType.Uuid) { Value = setup.SourceId });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Uuid) { Value = setup.StatusId });
        command.Parameters.Add(new NpgsqlParameter("source_type", NpgsqlDbType.Text) { Value = sourceType });
        command.Parameters.Add(new NpgsqlParameter("reporter", NpgsqlDbType.Uuid)
        {
            Value = CatalogApi.UserId,
        });
        command.Parameters.Add(new NpgsqlParameter("observed_at", NpgsqlDbType.TimestampTz)
        {
            Value = receivedAt,
        });
        command.Parameters.Add(new NpgsqlParameter("received_at", NpgsqlDbType.TimestampTz)
        {
            Value = receivedAt,
        });
        command.Parameters.Add(new NpgsqlParameter("expires_at", NpgsqlDbType.TimestampTz)
        {
            Value = receivedAt.AddHours(1),
        });

        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<Guid?> RunDetectorAsync(
        Guid? groupId,
        Guid objectId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var scope = _api.Factory.Services.CreateAsyncScope();
        var detector = scope.ServiceProvider.GetRequiredService<AnomalyDetector>();

        return await detector.CheckAsync(groupId, objectId, now, ct);
    }

    private Task<long> CountCasesAsync(Guid groupId, CancellationToken ct) =>
        CountByGroupAsync(groupId, openOnly: false, ct);

    private Task<long> CountOpenCasesAsync(Guid groupId, CancellationToken ct) =>
        CountByGroupAsync(groupId, openOnly: true, ct);

    private async Task<long> CountByGroupAsync(Guid groupId, bool openOnly, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        var stateFilter = openOnly ? "and state = 'open'" : string.Empty;
        await using var command = new NpgsqlCommand(
            $"""
             select count(*)
             from moderation_cases
             where kind = 'anomaly'
               and proposed->>'groupId' = @group_id
               {stateFilter}
             """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("group_id", NpgsqlDbType.Text)
        {
            Value = groupId.ToString(),
        });

        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private async Task<long> CountCasesByCategoryAsync(Guid categoryId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select count(*)
            from moderation_cases
            where kind = 'anomaly'
              and proposed->>'categoryId' = @category_id
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("category_id", NpgsqlDbType.Text)
        {
            Value = categoryId.ToString(),
        });

        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private async Task<string?> ReadProposedAsync(Guid caseId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            "select proposed::text from moderation_cases where id = @id",
            connection);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = caseId });

        var value = await command.ExecuteScalarAsync(ct);

        return value as string;
    }

    private sealed record Setup(
        Guid GroupId,
        Guid ObjectId,
        Guid SourceId,
        Guid StatusId,
        Guid? CategoryId = null);
}
