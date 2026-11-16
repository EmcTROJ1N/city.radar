using BuildingBlocks.Messaging;
using BuildingBlocks.Persistence;
using BuildingBlocks.Web;

using Catalog.Contracts;

using IdentityAccess.Contracts;

using Microsoft.EntityFrameworkCore;

using Observations.Domain;

using Sources.Contracts;

namespace Observations.Application;

/// <summary>Команда ручной отметки (<c>POST /observations</c>).</summary>
/// <param name="MapObjectId">Фиксация.</param>
/// <param name="SourceId">Один из источников, привязанных к фиксации.</param>
/// <param name="SourceStatusId">Статус из списка выбранного источника.</param>
/// <param name="Comment">Необязательный комментарий пользователя.</param>
/// <param name="Latitude">Широта браузера; только для расчёта расстояния.</param>
/// <param name="Longitude">Долгота браузера; только для расчёта расстояния.</param>
public sealed record CreateObservationCommand(
    Guid MapObjectId,
    Guid SourceId,
    Guid SourceStatusId,
    string? Comment,
    double? Latitude,
    double? Longitude);

/// <summary>Результат создания отметки для ответа API.</summary>
/// <param name="Id">Идентификатор наблюдения.</param>
/// <param name="MapObjectId">Фиксация.</param>
/// <param name="Status">Статус, который выставила отметка.</param>
/// <param name="ObservedAt">Серверное время наблюдения.</param>
/// <param name="ExpiresAt">Момент истечения актуальности.</param>
/// <param name="SourceType">Тип источника: <c>manual</c> или <c>moderator</c>.</param>
/// <param name="Comment">Комментарий пользователя.</param>
/// <param name="GeoDistanceM">Расстояние до фиксации, метры; <c>null</c> без геолокации.</param>
public sealed record CreatedObservation(
    Guid Id,
    Guid MapObjectId,
    PublicStatusView Status,
    DateTimeOffset ObservedAt,
    DateTimeOffset ExpiresAt,
    string SourceType,
    string? Comment,
    int? GeoDistanceM);

/// <summary>
/// Нарушение правил создания отметки, которое API переводит в Problem Details:
/// 404 — фиксации нет, 409 — у фиксации нет источников, 422 — остальные проверки.
/// </summary>
public sealed class ObservationRequestException(int statusCode, string field, string message)
    : Exception(message)
{
    /// <summary>HTTP-код ответа: 401, 404, 409 или 422.</summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>Поле запроса для <c>errors[].field</c>; для 404/409 не используется.</summary>
    public string Field { get; } = field;
}

/// <summary>
/// Создание ручной отметки (010, 014): проверки, наблюдение,
/// пересчёт проекции, outbox-событие для детектора аномалий и аудит модератора — одной
/// транзакцией и одним <c>SaveChanges</c>.
/// </summary>
/// <remarks>
/// Порядок записи: наблюдение, outbox и аудит добавляются в change tracker, затем
/// <c>SaveChanges</c> фиксирует их внутри транзакции, после чего пересчёт читает уже
/// сохранённое наблюдение и обновляет <c>current_statuses</c> в той же транзакции. Любой сбой
/// (в том числе пересчёта или outbox) откатывает всё: ни наблюдения, ни проекции, ни события.
/// </remarks>
public sealed class CreateObservationHandler(
    MayakDbContext db,
    IObservationRepository repository,
    IStatusRecalculator recalculator,
    IObservationReadStore readStore,
    IMapObjectDirectory mapObjects,
    ISourceDirectory sources,
    ICurrentUser currentUser,
    IGlobalSettings settings,
    IAuditLog audit,
    IOutbox outbox,
    TimeProvider time)
{
    private const string OutboxDestination = "api";
    private const string OutboxType = "ObservationCreated";

    /// <summary>Создаёт отметку и пересчитывает текущий статус фиксации.</summary>
    /// <exception cref="ObservationRequestException">Запрос не проходит проверки контракта.</exception>
    public async Task<CreatedObservation> HandleAsync(
        CreateObservationCommand command,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.UserId is not { } userId)
        {
            throw new ObservationRequestException(
                401,
                "authorization",
                "Войдите, чтобы поставить отметку");
        }

        var objectInfo = await mapObjects.GetInfoAsync(command.MapObjectId, ct);
        if (objectInfo is null)
        {
            throw new ObservationRequestException(
                404,
                "mapObjectId",
                $"Фиксация {command.MapObjectId} не существует");
        }

        if (!objectInfo.IsActive)
        {
            throw new ObservationRequestException(
                422,
                "mapObjectId",
                "Неактивную фиксацию отметить нельзя");
        }

        var sourceIds = new HashSet<Guid>(await mapObjects.GetSourceIdsAsync(command.MapObjectId, ct));
        if (objectInfo.CategoryId is { } categoryId)
        {
            // «Сайт» доступен фиксации неявно, без записи в map_object_sources.
            if (await sources.GetSiteSourceIdAsync(categoryId, ct) is { } siteSourceId)
            {
                sourceIds.Add(siteSourceId);
            }
        }

        if (sourceIds.Count == 0)
        {
            throw new ObservationRequestException(
                409,
                "sourceId",
                "У фиксации не задан тип, отметить её нельзя");
        }

        if (!sourceIds.Contains(command.SourceId))
        {
            throw new ObservationRequestException(
                422,
                "sourceId",
                "Источник недоступен для этой фиксации");
        }

        var policy = await sources.GetPolicyAsync(command.SourceId, ct);
        if (policy is null)
        {
            throw new ObservationRequestException(
                422,
                "sourceId",
                "Источник не существует");
        }

        if (!await sources.IsEnabledAsync(command.SourceId, ct))
        {
            throw new ObservationRequestException(
                422,
                "sourceId",
                "Источник выключен");
        }

        var statuses = await sources.GetStatusesAsync(command.SourceId, ct);
        var status = statuses.FirstOrDefault(candidate => candidate.Id == command.SourceStatusId);
        if (status is null)
        {
            throw new ObservationRequestException(
                422,
                "sourceStatusId",
                "Статус не принадлежит выбранному источнику");
        }

        var snapshot = await settings.GetAsync(ct);
        ValidateComment(command.Comment, snapshot.CommentMaxLength);

        var geoDistanceM = command.Latitude is { } latitude && command.Longitude is { } longitude
            ? await readStore.GetGeoDistanceMAsync(command.MapObjectId, latitude, longitude, ct)
            : null;

        var now = time.GetUtcNow();
        var kind = currentUser.Has(Permissions.ObservationRetract)
            ? SourceKind.Moderator
            : SourceKind.Manual;

        Observation observation;
        try
        {
            observation = Observation.Create(
                new ObservationDraft(
                    command.MapObjectId,
                    command.SourceId,
                    command.SourceStatusId,
                    kind,
                    ObservedAt: now,
                    ReceivedAt: now,
                    ReporterId: userId,
                    Comment: command.Comment),
                policy.Timeout,
                now);
        }
        catch (DomainException exception)
        {
            throw new ObservationRequestException(422, "sourceId", exception.Message);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        try
        {
            await repository.AddAsync(observation, ct, geoDistanceM);

            // Событие для детектора аномалий: destination api, сохраняется тем же
            // SaveChanges, что и наблюдение. groupId — группа фиксации или null.
            outbox.Enqueue(
                OutboxDestination,
                OutboxType,
                new
                {
                    observationId = observation.Id,
                    mapObjectId = observation.MapObjectId,
                    groupId = objectInfo.GroupId,
                    sourceKind = ToDb(observation.Kind),
                    at = now,
                });

            // Аудит только для действий модератора; пользовательские отметки в журнал не пишутся.
            if (observation.Kind == SourceKind.Moderator)
            {
                await audit.WriteAsync(
                    "observation.create",
                    "observation",
                    observation.Id.ToString(),
                    before: null,
                    after: new
                    {
                        mapObjectId = observation.MapObjectId,
                        sourceId = observation.SourceId,
                        sourceStatusId = observation.SourceStatusId,
                        sourceType = ToDb(observation.Kind),
                        observedAt = observation.ObservedAt,
                        expiresAt = observation.ExpiresAt,
                        comment = observation.Comment,
                        geoDistanceM,
                    },
                    ct: ct);
            }

            await db.SaveChangesAsync(ct);

            // Пересчёт видит уже сохранённое наблюдение и пишет проекцию в этой же транзакции.
            await recalculator.RecalculateAsync(observation.MapObjectId, now, ct);

            await transaction.CommitAsync(ct);

            return new CreatedObservation(
                observation.Id,
                observation.MapObjectId,
                new PublicStatusView(
                    status.Id,
                    status.Code,
                    status.Name,
                    status.Color,
                    status.Icon),
                observation.ObservedAt,
                observation.ExpiresAt,
                ToDb(observation.Kind),
                observation.Comment,
                geoDistanceM);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);

            throw;
        }
    }

    private static void ValidateComment(string? comment, int maxLength)
    {
        if (comment is null)
        {
            return;
        }

        if (comment.Length > maxLength)
        {
            throw new ObservationRequestException(
                422,
                "comment",
                $"Комментарий длиннее {maxLength} символов");
        }

        if (!TextValidator.IsValid(comment))
        {
            throw new ObservationRequestException(
                422,
                "comment",
                TextValidator.InvalidCharactersMessage);
        }
    }

    private static string ToDb(SourceKind kind) => kind switch
    {
        SourceKind.Manual => "manual",
        SourceKind.Moderator => "moderator",
        SourceKind.Telegram => "telegram",
        SourceKind.Max => "max",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный тип источника."),
    };
}
