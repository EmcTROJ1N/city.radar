using System.Net;

using Api.IntegrationTests.Catalog;
using Api.IntegrationTests.Infrastructure;

using BuildingBlocks.Persistence;

using IdentityAccess.Contracts;
using IdentityAccess.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;
using NpgsqlTypes;

using Observations.Application;
using Observations.Infrastructure;

using Shouldly;

namespace Api.IntegrationTests.Observations;

/// <summary>
/// Фоновый пересчёт по TTL. Истечение наблюдения и
/// переход Fresh → Aging должны попадать в проекцию без новых отметок, а повторный проход по
/// неизменившимся фиксациям не должен трогать <c>updated_at</c> (ETag). Часы управляемые:
/// <see cref="ExpirationSweeper"/> и пересчёт берут время только из <see cref="TimeProvider"/>.
/// Изоляция — новые источники, статусы и фиксации на каждый тест; общая БД не чистится.
/// </summary>
[Collection("postgres")]
public sealed class ExpirationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string SourcesPath = "/api/v1/sources";
    private const string ObjectsPath = "/api/v1/catalog/objects";

    private static readonly DateTimeOffset Start = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly TestClock _clock = new(Start);

    private CatalogApi _api = null!;
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _api = new CatalogApi(fixture);
        await _api.InitializeAsync();

        _provider = BuildProvider(_clock);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _api.DisposeAsync();
    }

    [Fact]
    public void Test_host_disables_background_sweeper()
    {
        _api.Factory.Services.GetRequiredService<ExpirationSweeperOptions>().SweeperEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Expired_observation_turns_status_to_unknown_within_one_sweep()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(token, ct: ct);
        var now = _clock.GetUtcNow();

        // До истечения: наблюдение актуально (expires_at = now + 5 минут), проекция не «неизвестно».
        await InsertObservationAsync(
            setup.ObjectId,
            setup.SourceId,
            setup.StatusA,
            now.AddMinutes(-10),
            expiresAt: now.AddMinutes(5),
            ct: ct);

        await RecalculateAsync(_provider, setup.ObjectId, now, ct);
        (await ReadProjectionAsync(setup.ObjectId, ct))!.SourceStatusId.ShouldBe(setup.StatusA);

        // Через 10 минут expires_at уже в прошлом: один проход обязан дать «неизвестно».
        _clock.Advance(TimeSpan.FromMinutes(10));

        var recalculated = await SweepAsync(_provider, ct);

        recalculated.ShouldBeGreaterThanOrEqualTo(1);

        var row = await ReadProjectionAsync(setup.ObjectId, ct);
        row.ShouldNotBeNull();
        row.SourceStatusId.ShouldBeNull();
        row.SourceId.ShouldBeNull();
        row.ObservationId.ShouldBeNull();
        row.Freshness.ShouldBe("stale");
        row.ObservationCount.ShouldBe(0);
    }

    [Fact]
    public async Task Freshness_moves_fresh_to_aging_without_new_observations()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(token, ct: ct);
        var now = _clock.GetUtcNow();

        // Таймаут 1 час, возраст 10 минут: Fresh (граница Fresh — 30 минут).
        await InsertObservationAsync(
            setup.ObjectId,
            setup.SourceId,
            setup.StatusA,
            now.AddMinutes(-10),
            expiresAt: now.AddMinutes(50),
            ct: ct);

        await RecalculateAsync(_provider, setup.ObjectId, now, ct);
        (await ReadProjectionAsync(setup.ObjectId, ct))!.Freshness.ShouldBe("fresh");

        // Возраст 35 минут: всё ещё актуально, но уже Aging; новых отметок не было.
        _clock.Advance(TimeSpan.FromMinutes(25));

        await SweepAsync(_provider, ct);

        var row = await ReadProjectionAsync(setup.ObjectId, ct);
        row.ShouldNotBeNull();
        row.SourceStatusId.ShouldBe(setup.StatusA);
        row.Freshness.ShouldBe("aging");
        row.ObservationCount.ShouldBe(1);
    }

    [Fact]
    public async Task Sweep_does_not_touch_unchanged_objects()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(token, ct: ct);
        var now = _clock.GetUtcNow();

        await InsertObservationAsync(
            setup.ObjectId,
            setup.SourceId,
            setup.StatusA,
            now.AddMinutes(-1),
            expiresAt: now.AddMinutes(59),
            ct: ct);

        await RecalculateAsync(_provider, setup.ObjectId, now, ct);
        var before = await ReadProjectionAsync(setup.ObjectId, ct);
        before.ShouldNotBeNull();

        // Через минуту итог и свежесть не меняются: пересчёт не должен обновлять updated_at,
        // иначе ETag карты прыгал бы на каждом проходе фоновой задачи.
        _clock.Advance(TimeSpan.FromMinutes(1));

        await SweepAsync(_provider, ct);

        var after = await ReadProjectionAsync(setup.ObjectId, ct);
        after.ShouldNotBeNull();
        after.UpdatedAt.ShouldBe(before.UpdatedAt);
        after.Freshness.ShouldBe("fresh");
        after.SourceStatusId.ShouldBe(setup.StatusA);
    }

    [Fact]
    public async Task Sweep_after_timeout_change_uses_expires_at_of_existing_observations()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSetupAsync(token, ct: ct);
        var now = _clock.GetUtcNow();
        var observedAt = now.AddMinutes(-10);
        var expiresAt = now.AddMinutes(50);

        await InsertObservationAsync(
            setup.ObjectId,
            setup.SourceId,
            setup.StatusA,
            observedAt,
            expiresAt: expiresAt,
            ct: ct);

        await RecalculateAsync(_provider, setup.ObjectId, now, ct);
        (await ReadProjectionAsync(setup.ObjectId, ct))!.Freshness.ShouldBe("fresh");

        // Смена таймаута источника действует на новые наблюдения, но не переписывает expires_at
        // существующего: новый таймаут 1 минута не должен «просрочить» отметку.
        await _api.ExecuteAsync(
            $"update source_policies set timeout = interval '1 minute' where source_id = '{setup.SourceId}'",
            ct);

        _clock.Advance(TimeSpan.FromMinutes(10));

        await SweepAsync(_provider, ct);

        var row = await ReadProjectionAsync(setup.ObjectId, ct);
        row.ShouldNotBeNull();
        row.SourceStatusId.ShouldBe(setup.StatusA);
        row.Freshness.ShouldBe("aging");
        (await ReadObservationExpiresAtAsync(setup.ObjectId, ct)).ShouldBe(expiresAt);
    }

    [Fact]
    public async Task Sweep_continues_after_failure_on_one_object()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var failing = await CreateSetupAsync(token, ct: ct);
        var healthy = await CreateSetupAsync(token, ct: ct);
        var now = _clock.GetUtcNow();

        foreach (var setup in new[] { failing, healthy })
        {
            await InsertObservationAsync(
                setup.ObjectId,
                setup.SourceId,
                setup.StatusA,
                now.AddMinutes(-10),
                expiresAt: now.AddMinutes(5),
                ct: ct);

            await RecalculateAsync(_provider, setup.ObjectId, now, ct);
        }

        _clock.Advance(TimeSpan.FromMinutes(10));

        await using var provider = BuildProvider(_clock, failing.ObjectId);

        var recalculated = await SweepAsync(provider, ct);

        try
        {
            // Упавшая фиксация не прерывает пачку: вторая всё равно пересчитана.
            recalculated.ShouldBeGreaterThanOrEqualTo(1);

            (await ReadProjectionAsync(healthy.ObjectId, ct))!.SourceStatusId.ShouldBeNull();
            (await ReadProjectionAsync(failing.ObjectId, ct))!.SourceStatusId.ShouldBe(failing.StatusA);
        }
        finally
        {
            // Снимаем «отравленную» проекцию, чтобы она не влияла на счётчики других тестов.
            await _api.ExecuteAsync(
                $"delete from current_statuses where map_object_id = '{failing.ObjectId}'",
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task Sweep_returns_count()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var now = _clock.GetUtcNow();
        var setups = new List<Setup>();

        for (var index = 0; index < 3; index++)
        {
            var setup = await CreateSetupAsync(token, ct: ct);
            setups.Add(setup);

            await InsertObservationAsync(
                setup.ObjectId,
                setup.SourceId,
                setup.StatusA,
                now.AddMinutes(-10),
                expiresAt: now.AddMinutes(5),
                ct: ct);

            await RecalculateAsync(_provider, setup.ObjectId, now, ct);
        }

        _clock.Advance(TimeSpan.FromMinutes(10));

        var first = await SweepAsync(_provider, ct);
        first.ShouldBeGreaterThanOrEqualTo(setups.Count);

        foreach (var setup in setups)
        {
            (await ReadProjectionAsync(setup.ObjectId, ct))!.SourceStatusId.ShouldBeNull();
        }

        // Повторный проход без новых изменений ничего не пересчитывает.
        var second = await SweepAsync(_provider, ct);
        second.ShouldBe(0);
    }

    private ServiceProvider BuildProvider(TimeProvider clock, Guid? failingMapObjectId = null)
    {
        var services = new ServiceCollection()
            .AddPersistence(fixture.ConnectionString)
            .AddIdentityAccess()
            .AddObservations()
            .AddSingleton(clock);

        if (failingMapObjectId is { } mapObjectId)
        {
            services.RemoveAll<IStatusRecalculator>();
            services.AddScoped<IStatusRecalculator>(provider => new FailingStatusRecalculator(
                provider.GetRequiredService<IObservationRepository>(),
                provider.GetRequiredService<ICurrentStatusWriter>(),
                provider.GetRequiredService<IGlobalSettings>(),
                mapObjectId));
        }

        return services.BuildServiceProvider();
    }

    private static Task<int> SweepAsync(IServiceProvider provider, CancellationToken ct) =>
        provider.GetRequiredService<ExpirationSweeper>().SweepOnceAsync(ct);

    private static async Task RecalculateAsync(
        IServiceProvider provider,
        Guid mapObjectId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var scope = provider.CreateAsyncScope();
        var calculator = scope.ServiceProvider.GetRequiredService<IStatusRecalculator>();

        await calculator.RecalculateAsync(mapObjectId, now, ct);
    }

    /// <summary>
    /// Создаёт источник (таймаут 1 час), два его статуса и фиксацию, привязанную к источнику.
    /// Данные уникальны на тест, поэтому очистка не нужна.
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
              {"name":"Источник {{suffix}}","sourceType":"telegram_chat","chatId":"@exp_{{suffix}}",
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
              {"name":"Фиксация {{suffix}}","objectTypeId":"00000000-0000-0000-0000-00000000aa01","coverageKind":"point",
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
    /// Вставляет наблюдение напрямую SQL: нужны произвольные <c>observed_at</c> и
    /// <c>expires_at</c>, которые API отметок вычисляет сам.
    /// </summary>
    private async Task<Guid> InsertObservationAsync(
        Guid mapObjectId,
        Guid sourceId,
        Guid sourceStatusId,
        DateTimeOffset observedAt,
        DateTimeOffset expiresAt,
        CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            insert into observations
              (map_object_id, source_id, source_status_id, source_type, reporter_id,
               observed_at, received_at, expires_at, is_retracted)
            values
              (@map_object_id, @source_id, @source_status_id, 'manual', @reporter_id,
               @observed_at, @observed_at, @expires_at, false)
            returning id
            """,
            connection);

        command.Parameters.Add(new NpgsqlParameter("map_object_id", NpgsqlDbType.Uuid) { Value = mapObjectId });
        command.Parameters.Add(new NpgsqlParameter("source_id", NpgsqlDbType.Uuid) { Value = sourceId });
        command.Parameters.Add(new NpgsqlParameter("source_status_id", NpgsqlDbType.Uuid) { Value = sourceStatusId });
        command.Parameters.Add(new NpgsqlParameter("reporter_id", NpgsqlDbType.Uuid) { Value = CatalogApi.ModeratorId });
        command.Parameters.Add(new NpgsqlParameter("observed_at", NpgsqlDbType.TimestampTz) { Value = observedAt });
        command.Parameters.Add(new NpgsqlParameter("expires_at", NpgsqlDbType.TimestampTz) { Value = expiresAt });

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

    private async Task<DateTimeOffset> ReadObservationExpiresAtAsync(
        Guid mapObjectId,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            "select expires_at from observations where map_object_id = @map_object_id",
            connection);
        command.Parameters.Add(new NpgsqlParameter("map_object_id", NpgsqlDbType.Uuid) { Value = mapObjectId });

        return new DateTimeOffset(
            DateTime.SpecifyKind(
                (DateTime)(await command.ExecuteScalarAsync(ct))!,
                DateTimeKind.Utc));
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

/// <summary>Пересчёт, который падает на одной заданной фиксации и работает как обычно на остальных.</summary>
internal sealed class FailingStatusRecalculator(
    IObservationRepository repository,
    ICurrentStatusWriter writer,
    IGlobalSettings settings,
    Guid failingMapObjectId) : IStatusRecalculator
{
    private readonly StatusRecalculator _inner = new(repository, writer, settings);

    public Task RecalculateAsync(Guid mapObjectId, DateTimeOffset now, CancellationToken ct) =>
        mapObjectId == failingMapObjectId
            ? throw new InvalidOperationException("Тестовый сбой пересчёта фиксации.")
            : _inner.RecalculateAsync(mapObjectId, now, ct);
}

/// <summary>Управляемые часы: и <see cref="ExpirationSweeper"/>, и пересчёт берут время отсюда.</summary>
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}
