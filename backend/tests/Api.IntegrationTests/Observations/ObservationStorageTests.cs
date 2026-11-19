using System.Net;

using Api.IntegrationTests.Catalog;
using Api.IntegrationTests.Infrastructure;

using BuildingBlocks.Persistence;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Observations.Application;
using Observations.Domain;
using Observations.Infrastructure;

using Shouldly;

namespace Api.IntegrationTests.Observations;

/// <summary>
/// Хранилище наблюдений: составной внешний ключ
/// «статус принадлежит источнику», неизменяемость через триггер <c>observations_guard</c>,
/// check-констрейнты, отзыв, проекция и реальные guard'ы удаления источника/статуса/фиксации
/// (409). Часть проверок идёт напрямую SQL — это контракт схемы, часть через
/// <see cref="IObservationRepository"/> и API. Изоляция — новые объекты/источники на тест.
/// </summary>
[Collection("postgres")]
public sealed class ObservationStorageTests(PostgresFixture fixture) : IAsyncLifetime
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
    public async Task Status_of_another_source_violates_composite_foreign_key()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var first = await CreateSourceWithStatusAsync(token, ct);
        var second = await CreateSourceWithStatusAsync(token, ct);

        var exception = await Should.ThrowAsync<PostgresException>(
            () => InsertObservationAsync(
                first.ObjectId,
                first.SourceId,
                second.StatusId,
                reporterId: CatalogApi.ModeratorId,
                ct: ct));

        exception.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        (await CountObservationsAsync(first.ObjectId, ct)).ShouldBe(0L);
    }

    [Fact]
    public async Task Update_of_observation_is_forbidden_by_trigger()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSourceWithStatusAsync(token, ct);
        var observationId = await InsertObservationAsync(
            setup.ObjectId,
            setup.SourceId,
            setup.StatusId,
            reporterId: CatalogApi.ModeratorId,
            ct: ct);

        var exception = await Should.ThrowAsync<PostgresException>(
            () => _api.ExecuteAsync(
                $"update observations set comment = 'подмена' where id = '{observationId}'",
                ct));

        exception.SqlState.ShouldBe(PostgresErrorCodes.RaiseException);
        exception.MessageText.ShouldContain("неизменяемо");
    }

    [Fact]
    public async Task Delete_of_observation_is_forbidden_by_trigger()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSourceWithStatusAsync(token, ct);
        var observationId = await InsertObservationAsync(
            setup.ObjectId,
            setup.SourceId,
            setup.StatusId,
            reporterId: CatalogApi.ModeratorId,
            ct: ct);

        var exception = await Should.ThrowAsync<PostgresException>(
            () => _api.ExecuteAsync($"delete from observations where id = '{observationId}'", ct));

        exception.SqlState.ShouldBe(PostgresErrorCodes.RaiseException);
        exception.MessageText.ShouldContain("только отзывать");
        (await CountObservationsAsync(setup.ObjectId, ct)).ShouldBe(1L);
    }

    [Fact]
    public async Task Retract_is_allowed_once_and_second_update_is_forbidden()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSourceWithStatusAsync(token, ct);
        var observationId = await InsertObservationAsync(
            setup.ObjectId,
            setup.SourceId,
            setup.StatusId,
            reporterId: CatalogApi.ModeratorId,
            ct: ct);

        var retractSql =
            $"""
             update observations
             set is_retracted = true, retracted_by = '{CatalogApi.ModeratorId}',
                 retracted_at = now(), retract_reason = 'Дубликат'
             where id = '{observationId}'
             """;

        await _api.ExecuteAsync(retractSql, ct);

        (await _api.CountAsync(
            $"""
             select count(*) from observations
             where id = '{observationId}' and is_retracted
               and retracted_by = '{CatalogApi.ModeratorId}'
               and retracted_at is not null and retract_reason = 'Дубликат'
             """,
            ct)).ShouldBe(1L);

        var exception = await Should.ThrowAsync<PostgresException>(
            () => _api.ExecuteAsync(retractSql, ct));

        exception.SqlState.ShouldBe(PostgresErrorCodes.RaiseException);
        exception.MessageText.ShouldContain("Отозванное наблюдение изменять нельзя");
    }

    [Fact]
    public async Task Manual_without_reporter_violates_check()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSourceWithStatusAsync(token, ct);

        var exception = await Should.ThrowAsync<PostgresException>(
            () => InsertObservationAsync(
                setup.ObjectId,
                setup.SourceId,
                setup.StatusId,
                sourceType: "manual",
                reporterId: null,
                ct: ct));

        exception.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Telegram_without_raw_message_violates_check()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSourceWithStatusAsync(token, ct);

        var exception = await Should.ThrowAsync<PostgresException>(
            () => InsertObservationAsync(
                setup.ObjectId,
                setup.SourceId,
                setup.StatusId,
                sourceType: "telegram",
                reporterId: null,
                ct: ct));

        exception.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Expires_at_not_after_observed_at_violates_check()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSourceWithStatusAsync(token, ct);
        var observedAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        var exception = await Should.ThrowAsync<PostgresException>(
            () => InsertObservationAsync(
                setup.ObjectId,
                setup.SourceId,
                setup.StatusId,
                reporterId: CatalogApi.ModeratorId,
                observedAt: observedAt,
                expiresAt: observedAt,
                ct: ct));

        exception.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Repository_round_trip_restores_observation_and_facts()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSourceWithStatusAsync(token, ct);

        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MayakDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<IObservationRepository>();

        var observedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var observation = Observation.Create(
            new ObservationDraft(
                setup.ObjectId,
                setup.SourceId,
                setup.StatusId,
                SourceKind.Manual,
                observedAt,
                DateTimeOffset.UtcNow,
                ReporterId: CatalogApi.ModeratorId,
                Confidence: 0.75f,
                Comment: "Вода есть"),
            TimeSpan.FromHours(1),
            DateTimeOffset.UtcNow);

        await repository.AddAsync(observation, ct);
        await db.SaveChangesAsync(ct);

        var stored = await repository.GetAsync(observation.Id, ct);

        stored.ShouldNotBeNull();
        stored.Id.ShouldBe(observation.Id);
        stored.MapObjectId.ShouldBe(setup.ObjectId);
        stored.SourceId.ShouldBe(setup.SourceId);
        stored.SourceStatusId.ShouldBe(setup.StatusId);
        stored.Kind.ShouldBe(SourceKind.Manual);
        stored.ReporterId.ShouldBe(CatalogApi.ModeratorId);
        stored.Confidence.ShouldBe(0.75f);
        stored.Comment.ShouldBe("Вода есть");
        stored.IsRetracted.ShouldBeFalse();
        (stored.ObservedAt - observedAt).Duration().ShouldBeLessThan(TimeSpan.FromMilliseconds(1));
        (stored.ExpiresAt - observation.ExpiresAt).Duration()
            .ShouldBeLessThan(TimeSpan.FromMilliseconds(1));

        var facts = await repository.GetFactsAsync(setup.ObjectId, ct);

        var fact = facts.ShouldHaveSingleItem();
        fact.Id.ShouldBe(observation.Id);
        fact.SourceId.ShouldBe(setup.SourceId);
        fact.SourceStatusId.ShouldBe(setup.StatusId);
        fact.Kind.ShouldBe(SourceKind.Manual);
        fact.IsRetracted.ShouldBeFalse();
        fact.SourceTimeout.ShouldBe(TimeSpan.FromHours(1));
        (fact.ExpiresAt - observation.ExpiresAt).Duration()
            .ShouldBeLessThan(TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Repository_retract_sets_fields_and_is_saved_by_caller()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSourceWithStatusAsync(token, ct);

        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MayakDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<IObservationRepository>();

        var observation = Observation.Create(
            new ObservationDraft(
                setup.ObjectId,
                setup.SourceId,
                setup.StatusId,
                SourceKind.Manual,
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow,
                ReporterId: CatalogApi.ModeratorId,
                Comment: "Вода есть"),
            TimeSpan.FromHours(1),
            DateTimeOffset.UtcNow);

        await repository.AddAsync(observation, ct);
        await db.SaveChangesAsync(ct);

        var retractedAt = DateTimeOffset.UtcNow;
        await repository.RetractAsync(
            observation.Id,
            CatalogApi.ModeratorId,
            "Наблюдение ошибочно",
            retractedAt,
            ct);

        // Репозиторий не сохраняет сам: поля отзыва ещё не в БД — это сделает вызывающий хендлер.
        (await _api.CountAsync(
            $"select count(*) from observations where id = '{observation.Id}' and is_retracted",
            ct)).ShouldBe(0L);

        await db.SaveChangesAsync(ct);

        var stored = await repository.GetAsync(observation.Id, ct);

        stored.ShouldNotBeNull();
        stored.IsRetracted.ShouldBeTrue();

        (await _api.CountAsync(
            $"""
             select count(*) from observations
             where id = '{observation.Id}' and is_retracted
               and retracted_by = '{CatalogApi.ModeratorId}'
               and retract_reason = 'Наблюдение ошибочно'
               and retracted_at is not null
             """,
            ct)).ShouldBe(1L);

        await Should.ThrowAsync<DomainException>(() => repository.RetractAsync(
            observation.Id,
            CatalogApi.ModeratorId,
            "Повторно",
            retractedAt,
            ct));
    }

    [Fact]
    public async Task Delete_source_with_observations_409()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSourceWithStatusAsync(token, ct);
        await InsertObservationAsync(
            setup.ObjectId,
            setup.SourceId,
            setup.StatusId,
            reporterId: CatalogApi.ModeratorId,
            ct: ct);

        using var response = await _api.SendAsync(
            HttpMethod.Delete,
            $"{SourcesPath}/{setup.SourceId}",
            token,
            json: null,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = await CatalogApi.ReadJsonAsync(response, ct);
        problem.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("наблюдения");

        (await _api.CountAsync(
            $"select count(*) from sources where id = '{setup.SourceId}'",
            ct)).ShouldBe(1L);
    }

    [Fact]
    public async Task Delete_source_status_with_observations_409()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSourceWithStatusAsync(token, ct);
        await InsertObservationAsync(
            setup.ObjectId,
            setup.SourceId,
            setup.StatusId,
            reporterId: CatalogApi.ModeratorId,
            ct: ct);

        using var response = await _api.SendAsync(
            HttpMethod.Delete,
            $"{SourcesPath}/{setup.SourceId}/statuses/{setup.StatusId}",
            token,
            json: null,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = await CatalogApi.ReadJsonAsync(response, ct);
        problem.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("наблюдения");

        (await _api.CountAsync(
            $"select count(*) from source_statuses where id = '{setup.StatusId}'",
            ct)).ShouldBe(1L);
    }

    [Fact]
    public async Task Delete_map_object_with_observations_409()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var setup = await CreateSourceWithStatusAsync(token, ct);
        await InsertObservationAsync(
            setup.ObjectId,
            setup.SourceId,
            setup.StatusId,
            reporterId: CatalogApi.ModeratorId,
            ct: ct);

        using var response = await _api.SendAsync(
            HttpMethod.Delete,
            $"{ObjectsPath}/{setup.ObjectId}",
            token,
            json: null,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = await CatalogApi.ReadJsonAsync(response, ct);
        problem.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("наблюдения");

        (await _api.CountAsync(
            $"select count(*) from map_objects where id = '{setup.ObjectId}'",
            ct)).ShouldBe(1L);
    }

    private ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddPersistence(fixture.ConnectionString)
            .AddObservations()
            .BuildServiceProvider();

    /// <summary>
    /// Создаёт источник (таймаут 1 час), статус этого источника и фиксацию, привязанную к нему.
    /// </summary>
    private async Task<(Guid SourceId, Guid StatusId, Guid ObjectId)> CreateSourceWithStatusAsync(
        string token,
        CancellationToken ct)
    {
        var suffix = CatalogApi.NewSuffix();
        var sourceId = await _api.CreateSourceAsync(token, ct: ct);

        Guid statusId;
        using (var response = await _api.SendAsync(
            HttpMethod.Post,
            $"{SourcesPath}/{sourceId}/statuses",
            token,
            $$"""
              {"code":"water_{{suffix}}","name":"Вода {{suffix}}","description":"Подача идёт",
               "color":"ok","icon":"drop"}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            statusId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
        }

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

        return (sourceId, statusId, objectId);
    }

    private async Task<Guid> InsertObservationAsync(
        Guid mapObjectId,
        Guid sourceId,
        Guid sourceStatusId,
        string sourceType = "manual",
        Guid? reporterId = null,
        DateTimeOffset? observedAt = null,
        DateTimeOffset? expiresAt = null,
        Guid? rawMessageId = null,
        CancellationToken ct = default)
    {
        var observed = observedAt ?? DateTimeOffset.UtcNow;
        var expires = expiresAt ?? observed.AddHours(1);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            insert into observations
              (map_object_id, source_id, source_status_id, source_type, reporter_id,
               observed_at, received_at, expires_at, raw_message_id)
            values
              (@object, @source, @status, @type, @reporter, @observed, @observed, @expires, @raw)
            returning id
            """,
            connection);
        command.Parameters.AddWithValue("object", mapObjectId);
        command.Parameters.AddWithValue("source", sourceId);
        command.Parameters.AddWithValue("status", sourceStatusId);
        command.Parameters.AddWithValue("type", sourceType);
        command.Parameters.AddWithValue("reporter", (object?)reporterId ?? DBNull.Value);
        command.Parameters.AddWithValue("observed", observed);
        command.Parameters.AddWithValue("expires", expires);
        command.Parameters.AddWithValue("raw", (object?)rawMessageId ?? DBNull.Value);

        return (Guid)(await command.ExecuteScalarAsync(ct))!;
    }

    private Task<long> CountObservationsAsync(Guid mapObjectId, CancellationToken ct) =>
        _api.CountAsync(
            $"select count(*) from observations where map_object_id = '{mapObjectId}'",
            ct);
}
