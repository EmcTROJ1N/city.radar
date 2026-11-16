using System.Net;

using Api.IntegrationTests.Catalog;
using Api.IntegrationTests.Infrastructure;

using BuildingBlocks.Persistence;

using IdentityAccess.Core;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;
using NpgsqlTypes;

using Observations.Application;
using Observations.Domain;
using Observations.Infrastructure;

using Shouldly;

namespace Api.IntegrationTests.Observations;

/// <summary>
/// Пересчёт проекции и конкурентность (Review Focus 2). Проекция
/// <c>current_statuses</c> пересчитывается из таблицы <c>observations</c> целиком под
/// advisory lock по фиксации; <c>updated_at</c> меняется только при изменении итога.
/// Хост Api и DI-провайдер поднимаются на общей БД коллекции; изоляция — новые источники,
/// статусы и фиксации на каждый тест, настройки Fresh восстанавливаются после теста.
/// </summary>
[Collection("postgres")]
public sealed class RecalculationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string SourcesPath = "/api/v1/sources";
    private const string ObjectsPath = "/api/v1/catalog/objects";
    private const string FreshRatioKey = "freshness_fresh_ratio";

    private CatalogApi _api = null!;

    /// <summary>
    /// Провайдер прикладных сервисов на той же БД: пересчёт вызывается напрямую, как из
    /// хендлера отметки, а не через HTTP — эндпоинтов у ещё нет.
    /// </summary>
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _api = new CatalogApi(fixture);
        await _api.InitializeAsync();

        _provider = new ServiceCollection()
            .AddPersistence(fixture.ConnectionString)
            .AddIdentityAccess()
            .AddObservations()
            .BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _api.DisposeAsync();
    }

    [Fact]
    public async Task Recalculate_writes_projection_row()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(token, ct: ct);
        var now = DateTimeOffset.UtcNow;
        var observedAt = now.AddMinutes(-1);

        var observationId = await InsertObservationAsync(
            setup.ObjectId,
            setup.SourceId,
            setup.StatusA,
            observedAt,
            ct: ct);

        await RecalculateAsync(setup.ObjectId, now, ct);

        var row = await ReadProjectionAsync(setup.ObjectId, ct);

        row.ShouldNotBeNull();
        row.SourceStatusId.ShouldBe(setup.StatusA);
        row.SourceId.ShouldBe(setup.SourceId);
        row.ObservationId.ShouldBe(observationId);
        row.ObservedAt.ShouldNotBeNull().ShouldBe(observedAt, TimeSpan.FromMilliseconds(1));
        row.Freshness.ShouldBe("fresh");
        row.SourceType.ShouldBe("manual");
        row.ObservationCount.ShouldBe(1);
        row.UpdatedAt.ShouldBe(now, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Recalculate_without_observations_writes_unknown_stale()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(token, ct: ct);
        var now = DateTimeOffset.UtcNow;

        // Строки проекции ещё нет: upsert обязан её создать, а не пропустить пересчёт.
        (await ReadProjectionAsync(setup.ObjectId, ct)).ShouldBeNull();

        await RecalculateAsync(setup.ObjectId, now, ct);

        var row = await ReadProjectionAsync(setup.ObjectId, ct);

        row.ShouldNotBeNull();
        row.SourceStatusId.ShouldBeNull();
        row.SourceId.ShouldBeNull();
        row.ObservationId.ShouldBeNull();
        row.ObservedAt.ShouldBeNull();
        row.Freshness.ShouldBe("stale");
        row.SourceType.ShouldBeNull();
        row.ObservationCount.ShouldBe(0);
    }

    [Fact]
    public async Task Unchanged_result_does_not_touch_updated_at()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(token, ct: ct);
        var now = DateTimeOffset.UtcNow;

        await InsertObservationAsync(
            setup.ObjectId,
            setup.SourceId,
            setup.StatusA,
            now.AddMinutes(-1),
            ct: ct);

        await RecalculateAsync(setup.ObjectId, now, ct);
        var first = await ReadProjectionAsync(setup.ObjectId, ct);
        first.ShouldNotBeNull();

        await Task.Delay(50);

        // Тот же итог при том же now: updated_at — версия проекции для ETag, он не должен
        // меняться от самого факта повторного пересчёта.
        await RecalculateAsync(setup.ObjectId, now, ct);
        var second = await ReadProjectionAsync(setup.ObjectId, ct);

        second.ShouldNotBeNull();
        second.UpdatedAt.ShouldBe(first.UpdatedAt);
        second.ObservationCount.ShouldBe(first.ObservationCount);
    }

    [Fact]
    public async Task Parallel_recalculations_for_same_object_end_with_latest_observation()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(token, ct: ct);
        var now = DateTimeOffset.UtcNow;
        var baseTime = now.AddMinutes(-10);

        // Наблюдения вставляются в случайном порядке и параллельно: пересчёт идёт из таблицы
        // целиком под advisory lock, поэтому победитель не зависит от порядка вставок.
        var offsets = Enumerable.Range(0, 50).OrderBy(_ => Random.Shared.Next()).ToArray();

        await Parallel.ForEachAsync(
            offsets,
            new ParallelOptions { MaxDegreeOfParallelism = 50, CancellationToken = ct },
            async (offset, itemToken) =>
            {
                await using var scope = _provider.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<MayakDbContext>();
                var repository = scope.ServiceProvider.GetRequiredService<IObservationRepository>();
                var calculator = scope.ServiceProvider.GetRequiredService<IStatusRecalculator>();

                await using var transaction = await db.Database.BeginTransactionAsync(itemToken);

                var observedAt = baseTime.AddSeconds(offset);
                var observation = Observation.Create(
                    new ObservationDraft(
                        setup.ObjectId,
                        setup.SourceId,
                        offset % 2 == 0 ? setup.StatusA : setup.StatusB,
                        SourceKind.Manual,
                        observedAt,
                        observedAt,
                        ReporterId: CatalogApi.ModeratorId),
                    TimeSpan.FromHours(1),
                    now);

                await repository.AddAsync(observation, itemToken);
                await db.SaveChangesAsync(itemToken);

                // Внутри уже открытой транзакции: тот же путь, что у хендлера отметки.
                await calculator.RecalculateAsync(setup.ObjectId, now, itemToken);

                await transaction.CommitAsync(itemToken);
            });

        var row = await ReadProjectionAsync(setup.ObjectId, ct);

        row.ShouldNotBeNull();
        row.ObservationCount.ShouldBe(50);
        row.SourceStatusId.ShouldBe(setup.StatusB);
        row.ObservedAt.ShouldNotBeNull().ShouldBe(
            baseTime.AddSeconds(49),
            TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Observation_count_matches_actual()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(token, ct: ct);
        var now = DateTimeOffset.UtcNow;

        await InsertObservationAsync(setup.ObjectId, setup.SourceId, setup.StatusA, now.AddMinutes(-3), ct: ct);
        await InsertObservationAsync(setup.ObjectId, setup.SourceId, setup.StatusB, now.AddMinutes(-2), ct: ct);

        // Отозванное и истёкшее в счёт актуальных наблюдений не идут.
        await InsertObservationAsync(
            setup.ObjectId,
            setup.SourceId,
            setup.StatusA,
            now.AddMinutes(-1),
            isRetracted: true,
            ct: ct);
        await InsertObservationAsync(
            setup.ObjectId,
            setup.SourceId,
            setup.StatusB,
            now.AddHours(-3),
            expiresAt: now.AddHours(-2),
            ct: ct);

        await RecalculateAsync(setup.ObjectId, now, ct);

        var row = await ReadProjectionAsync(setup.ObjectId, ct);

        row.ShouldNotBeNull();
        row.ObservationCount.ShouldBe(2);
        row.SourceStatusId.ShouldBe(setup.StatusB);
    }

    [Fact]
    public async Task Retracted_observation_is_excluded()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(token, ct: ct);
        var now = DateTimeOffset.UtcNow;

        var actualId = await InsertObservationAsync(
            setup.ObjectId,
            setup.SourceId,
            setup.StatusA,
            now.AddMinutes(-2),
            ct: ct);

        // Отозванное наблюдение свежее победителя, но учитываться не должно.
        await InsertObservationAsync(
            setup.ObjectId,
            setup.SourceId,
            setup.StatusB,
            now.AddMinutes(-1),
            isRetracted: true,
            ct: ct);

        await RecalculateAsync(setup.ObjectId, now, ct);

        var row = await ReadProjectionAsync(setup.ObjectId, ct);

        row.ShouldNotBeNull();
        row.SourceStatusId.ShouldBe(setup.StatusA);
        row.ObservationId.ShouldBe(actualId);
        row.ObservationCount.ShouldBe(1);
    }

    [Fact]
    public async Task Expired_by_now_is_excluded()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(token, ct: ct);
        var now = DateTimeOffset.UtcNow;

        // Таймаут источника 1 час: наблюдение трёхчасовой давности уже не актуально.
        await InsertObservationAsync(
            setup.ObjectId,
            setup.SourceId,
            setup.StatusA,
            now.AddHours(-3),
            expiresAt: now.AddHours(-2),
            ct: ct);

        await RecalculateAsync(setup.ObjectId, now, ct);

        var row = await ReadProjectionAsync(setup.ObjectId, ct);

        row.ShouldNotBeNull();
        row.SourceStatusId.ShouldBeNull();
        row.Freshness.ShouldBe("stale");
        row.ObservationCount.ShouldBe(0);
    }

    [Fact]
    public async Task Freshness_uses_fresh_ratio_from_settings()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(token, ct: ct);
        var now = DateTimeOffset.UtcNow;

        // Возраст 40 минут при таймауте 60 минут: по умолчанию Fresh до 30 минут → Aging.
        await InsertObservationAsync(
            setup.ObjectId,
            setup.SourceId,
            setup.StatusA,
            now.AddMinutes(-40),
            ct: ct);

        try
        {
            await RecalculateAsync(setup.ObjectId, now, ct);
            (await ReadProjectionAsync(setup.ObjectId, ct))!.Freshness.ShouldBe("aging");

            await _api.ExecuteAsync(
                $"insert into global_settings (key, value) values ('{FreshRatioKey}', '0.8') "
                + "on conflict (key) do update set value = excluded.value",
                ct);

            // 0.8 от 60 минут = 48 минут: то же наблюдение теперь Fresh.
            await RecalculateAsync(setup.ObjectId, now, ct);
            (await ReadProjectionAsync(setup.ObjectId, ct))!.Freshness.ShouldBe("fresh");
        }
        finally
        {
            await _api.ExecuteAsync(
                $"delete from global_settings where key = '{FreshRatioKey}'",
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task Two_objects_do_not_block_each_other()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var first = await CreateSetupAsync(token, ct: ct);
        var second = await CreateSetupAsync(token, ct: ct);
        var now = DateTimeOffset.UtcNow;

        // Держим блокировку первой фиксации в отдельной транзакции и проверяем, что вторая
        // фиксация пересчитывается параллельно: ключ блокировки — сама фиксация, не глобальный.
        await using var holder = new NpgsqlConnection(fixture.ConnectionString);
        await holder.OpenAsync(ct);
        await using var holderTransaction = await holder.BeginTransactionAsync(ct);

        await using (var lockCommand = new NpgsqlCommand(
            "select pg_advisory_xact_lock(hashtext(cast(@map_object_id as text)))",
            holder,
            holderTransaction))
        {
            lockCommand.Parameters.Add(new NpgsqlParameter("map_object_id", NpgsqlDbType.Uuid)
            {
                Value = first.ObjectId,
            });

            await lockCommand.ExecuteNonQueryAsync(ct);
        }

        // Вторая фиксация не ждёт первую: если бы ключ был общим, вызов не завершился бы.
        using (var secondCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            await RecalculateAsync(second.ObjectId, now, secondCts.Token);
        }

        (await ReadProjectionAsync(second.ObjectId, ct)).ShouldNotBeNull();

        // Контроль: та же фиксация под блокировкой действительно ждёт — иначе проверка выше пустая.
        using var firstCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var blocked = Task.Run(async () =>
        {
            await RecalculateAsync(first.ObjectId, now, firstCts.Token);
        });

        await Task.Delay(TimeSpan.FromSeconds(1));
        blocked.IsCompleted.ShouldBeFalse();

        await Should.ThrowAsync<Exception>(async () => await blocked.WaitAsync(TimeSpan.FromSeconds(15)));
        blocked.IsCompleted.ShouldBeTrue("пересчёт под чужой блокировкой должен прерваться по отмене");

        await holderTransaction.RollbackAsync(ct);
    }

    private async Task RecalculateAsync(Guid mapObjectId, DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = _provider.CreateAsyncScope();
        var calculator = scope.ServiceProvider.GetRequiredService<IStatusRecalculator>();

        await calculator.RecalculateAsync(mapObjectId, now, ct);
    }

    /// <summary>
    /// Создаёт источник (таймаут по умолчанию 1 час), два его статуса и фиксацию, привязанную
    /// к источнику. Данные уникальны на тест, поэтому очистка не нужна.
    /// </summary>
    private async Task<Setup> CreateSetupAsync(
        string token,
        int timeoutSeconds = 3600,
        CancellationToken ct = default)
    {
        var suffix = CatalogApi.NewSuffix();

        Guid sourceId;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            SourcesPath,
            token,
            $$"""
              {"name":"Источник {{suffix}}","sourceType":"telegram_chat","chatId":"@rc_{{suffix}}",
               "timeoutSeconds":{{timeoutSeconds}},"userPrompt":""}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            sourceId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
        }

        var statusA = await CreateStatusAsync(token, sourceId, $"{suffix}a", ct);
        var statusB = await CreateStatusAsync(token, sourceId, $"{suffix}b", ct);

        Guid objectId;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            ObjectsPath,
            token,
            $$"""
              {"name":"Фиксация {{suffix}}","coverageKind":"point",
               "geometry":{"type":"Point","coordinates":[37.80,48.01]},
               "sourceIds":["{{sourceId}}"]}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            objectId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
        }

        return new Setup(sourceId, objectId, statusA, statusB);
    }

    private async Task<Guid> CreateStatusAsync(
        string token,
        Guid sourceId,
        string suffix,
        CancellationToken ct)
    {
        using var response = await _api.SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{sourceId}/statuses",
            token,
            $$"""
              {"code":"water_{{suffix}}","name":"Вода {{suffix}}","description":"Подача идёт",
               "color":"ok","icon":"drop"}
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Вставляет наблюдение напрямую SQL: датасет должен быть произвольным по времени,
    /// отозванным и истёкшим — API отметок появится позже.
    /// </summary>
    private async Task<Guid> InsertObservationAsync(
        Guid mapObjectId,
        Guid sourceId,
        Guid sourceStatusId,
        DateTimeOffset observedAt,
        DateTimeOffset? receivedAt = null,
        DateTimeOffset? expiresAt = null,
        bool isRetracted = false,
        CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            insert into observations
              (map_object_id, source_id, source_status_id, source_type, reporter_id,
               observed_at, received_at, expires_at,
               is_retracted, retracted_by, retracted_at, retract_reason)
            values
              (@map_object_id, @source_id, @source_status_id, 'manual', @reporter_id,
               @observed_at, @received_at, @expires_at,
               @is_retracted, @retracted_by, @retracted_at, @retract_reason)
            returning id
            """,
            connection);

        command.Parameters.Add(new NpgsqlParameter("map_object_id", NpgsqlDbType.Uuid) { Value = mapObjectId });
        command.Parameters.Add(new NpgsqlParameter("source_id", NpgsqlDbType.Uuid) { Value = sourceId });
        command.Parameters.Add(new NpgsqlParameter("source_status_id", NpgsqlDbType.Uuid) { Value = sourceStatusId });
        command.Parameters.Add(new NpgsqlParameter("reporter_id", NpgsqlDbType.Uuid) { Value = CatalogApi.ModeratorId });
        command.Parameters.Add(new NpgsqlParameter("observed_at", NpgsqlDbType.TimestampTz) { Value = observedAt });
        command.Parameters.Add(new NpgsqlParameter("received_at", NpgsqlDbType.TimestampTz)
        {
            Value = receivedAt ?? observedAt,
        });
        command.Parameters.Add(new NpgsqlParameter("expires_at", NpgsqlDbType.TimestampTz)
        {
            Value = expiresAt ?? observedAt.AddHours(1),
        });
        command.Parameters.Add(new NpgsqlParameter("is_retracted", NpgsqlDbType.Boolean) { Value = isRetracted });
        command.Parameters.Add(new NpgsqlParameter("retracted_by", NpgsqlDbType.Uuid)
        {
            Value = isRetracted ? CatalogApi.ModeratorId : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("retracted_at", NpgsqlDbType.TimestampTz)
        {
            Value = isRetracted ? observedAt : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("retract_reason", NpgsqlDbType.Text)
        {
            Value = isRetracted ? "Дубликат" : DBNull.Value,
        });

        return (Guid)(await command.ExecuteScalarAsync(ct))!;
    }

    private async Task<ProjectionRow?> ReadProjectionAsync(Guid mapObjectId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select source_status_id, source_id, observation_id, observed_at,
                   freshness, source_type, observation_count, updated_at
            from current_statuses
            where map_object_id = @map_object_id
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("map_object_id", NpgsqlDbType.Uuid) { Value = mapObjectId });

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new ProjectionRow(
            reader.IsDBNull(0) ? null : reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            ReadTimestamp(reader, 3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt32(6),
            ReadTimestamp(reader, 7)!.Value);
    }

    private static DateTimeOffset? ReadTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(reader.GetFieldValue<DateTime>(ordinal), DateTimeKind.Utc));

    private sealed record Setup(
        Guid SourceId,
        Guid ObjectId,
        Guid StatusA,
        Guid StatusB);

    private sealed record ProjectionRow(
        Guid? SourceStatusId,
        Guid? SourceId,
        Guid? ObservationId,
        DateTimeOffset? ObservedAt,
        string Freshness,
        string? SourceType,
        int ObservationCount,
        DateTimeOffset UpdatedAt);
}
