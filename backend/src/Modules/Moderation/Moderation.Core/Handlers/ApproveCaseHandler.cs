using System.Data;
using System.Data.Common;
using System.Text.Json;

using BuildingBlocks.Persistence;
using BuildingBlocks.Web;

using Catalog.Contracts;

using IdentityAccess.Contracts;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Moderation.Core.Domain;
using Moderation.Core.Services;

using Observations.Contracts;

using Sources.Contracts;
namespace Moderation.Core.Handlers;

/// <summary>
/// Тело <c>POST /moderation/cases/{id}/approve</c>.
/// </summary>
/// <param name="MapObjectIds">
/// Выбранные фиксации. Обязательны для <c>object_not_found</c>; для остальных видов заменяют
/// предложенную системой фиксацию (или список всех фиксаций источника).
/// </param>
/// <param name="SourceStatusId">Исправленный статус из списка источника; иначе берётся предложенный код.</param>
internal sealed record ApproveCaseRequest(
    IReadOnlyList<Guid> MapObjectIds,
    Guid? SourceStatusId);

/// <summary>
/// Принятие кейса модерации: создаёт наблюдение по каждой
/// выбранной фиксации, пересчитывает проекции и закрывает кейс.
/// </summary>
/// <remarks>
/// <para>
/// Для <c>low_confidence</c>/<c>invalid_status</c>/<c>invalid_response</c> фиксации берутся из
/// предложения системы, из тела запроса (если модератор выбрал вручную) либо — когда режим
/// «по тексту» у источника выключен или фиксация одна — по всем фиксациям источника.
/// Для <c>object_not_found</c> выбор обязателен.
/// </para>
/// <para>
/// <c>anomaly</c> и <c>complaint</c> одобряются без создания наблюдения: это пометка
/// «обработано» для кейсов, у которых нет предложенного статуса.
/// </para>
/// <para>
/// Наблюдения создаются с <c>ObservedAt = raw_messages.posted_at</c>: одобрение старого
/// сообщения не делает статус свежим. Наблюдение, аудит и смена состояния кейса — одна
/// транзакция и один коммит.
/// </para>
/// </remarks>
internal sealed class ApproveCaseHandler(
    MayakDbContext db,
    IObservationFactory observations,
    ISourceDirectory sources,
    IMapObjectDirectory mapObjects,
    IAuditLog audit,
    ICurrentUser currentUser,
    TimeProvider time,
    ModerationCaseService caseService)
{
    private const string AuditAction = "moderation.case.approve";
    private const string EntityType = "moderation_case";

    public async Task<IResult> HandleAsync(
        Guid id,
        ApproveCaseRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (currentUser.UserId is not { } userId)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Не авторизован",
                detail: "Войдите, чтобы обработать кейс",
                type: "about:blank");
        }

        var moderationCase = await db.Set<ModerationCase>()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (moderationCase is null)
        {
            return ProblemResponses.NotFound($"Кейс {id} не существует");
        }

        if (moderationCase.State != ModerationCaseStates.Open)
        {
            return ProblemResponses.Conflict(
                $"Кейс {id} уже обработан: состояние {moderationCase.State}");
        }

        if (IsProcessedWithoutObservation(moderationCase.Kind))
        {
            return await CloseProcessedAsync(moderationCase, userId, request, ct);
        }

        if (moderationCase.SourceId is not { } sourceId)
        {
            return ProblemResponses.Validation(
                new ValidationError("sourceId", "У кейса нет источника: создать наблюдение нельзя"));
        }

        if (moderationCase.RawMessageId is not { } rawMessageId)
        {
            return ProblemResponses.Validation(
                new ValidationError("rawMessageId", "У кейса нет исходного сообщения"));
        }

        var policy = await sources.GetPolicyAsync(sourceId, ct);
        if (policy is null)
        {
            return ProblemResponses.Validation(
                new ValidationError("sourceId", "Источник кейса не существует"));
        }

        var proposed = ReadProposed(moderationCase.Proposed);

        var sourceObjects = await mapObjects.GetObjectsOfSourceAsync(sourceId, ct);
        var sourceObjectIds = sourceObjects.Select(item => item.Id).ToHashSet();

        var targets = ResolveTargets(
            moderationCase,
            request,
            proposed,
            sourceObjectIds,
            sourceObjects.Count,
            policy.LinkByText,
            out var selectionError);
        if (selectionError is not null)
        {
            return selectionError;
        }

        var statuses = await sources.GetStatusesAsync(sourceId, ct);
        var status = ResolveStatus(request, proposed, statuses, out var statusError);
        if (statusError is not null || status is null)
        {
            return statusError ?? ProblemResponses.Validation(
                new ValidationError("sourceStatusId", "Статус не найден среди статусов источника"));
        }

        if (!TryMapKind(policy.SourceType, out var kind))
        {
            return ProblemResponses.Validation(
                new ValidationError("sourceId", "Источник имеет неизвестный тип"));
        }

        var postedAt = await ReadPostedAtAsync(rawMessageId, ct);
        if (postedAt is null)
        {
            return ProblemResponses.Validation(
                new ValidationError("rawMessageId", "Исходное сообщение не найдено"));
        }

        var now = time.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        try
        {
            var observationIds = new List<Guid>(targets.Count);
            foreach (var target in targets)
            {
                var observationId = await observations.CreateFromModerationAsync(
                    new ModeratedObservation(
                        target,
                        sourceId,
                        status.Id,
                        rawMessageId,
                        postedAt.Value,
                        kind,
                        proposed.Confidence,
                        proposed.Address),
                    ct);

                observationIds.Add(observationId);
            }

            var before = new
            {
                state = moderationCase.State,
                mapObjectId = moderationCase.MapObjectId,
                observationId = moderationCase.ObservationId,
            };

            moderationCase.State = ModerationCaseStates.Approved;
            moderationCase.MapObjectId ??= targets[0];
            moderationCase.ObservationId = observationIds[0];
            moderationCase.ResolvedAt = now;
            moderationCase.ResolvedBy = userId;
            moderationCase.Resolution = JsonSerializer.Serialize(
                new
                {
                    action = "approve",
                    sourceStatusId = status.Id,
                    sourceStatusCode = status.Code,
                    mapObjectIds = targets,
                    observationIds,
                    observedAt = postedAt.Value,
                },
                JsonSerializerOptions.Web);

            await audit.WriteAsync(
                AuditAction,
                EntityType,
                moderationCase.Id.ToString(),
                before,
                new
                {
                    state = moderationCase.State,
                    mapObjectId = moderationCase.MapObjectId,
                    observationId = moderationCase.ObservationId,
                    observationIds,
                    sourceStatusId = status.Id,
                    resolvedAt = now,
                    resolvedBy = userId,
                },
                ct: ct);

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await transaction.RollbackAsync(CancellationToken.None);

            throw;
        }

        return await caseService.GetAsync(id, ct);
    }

    /// <summary>Кейсы, у которых нет предложенного статуса: одобрение — только пометка «обработано».</summary>
    private static bool IsProcessedWithoutObservation(string kind) =>
        kind is ModerationCaseKinds.Anomaly or ModerationCaseKinds.Complaint;

    private async Task<IResult> CloseProcessedAsync(
        ModerationCase moderationCase,
        Guid userId,
        ApproveCaseRequest request,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var before = new { state = moderationCase.State };

        moderationCase.State = ModerationCaseStates.Approved;
        moderationCase.ResolvedAt = now;
        moderationCase.ResolvedBy = userId;
        moderationCase.Resolution = JsonSerializer.Serialize(
            new
            {
                action = "approve",
                note = "processed",
                mapObjectIds = request.MapObjectIds,
            },
            JsonSerializerOptions.Web);

        await audit.WriteAsync(
            AuditAction,
            EntityType,
            moderationCase.Id.ToString(),
            before,
            new
            {
                state = moderationCase.State,
                resolvedAt = now,
                resolvedBy = userId,
                note = "processed",
            },
            ct: ct);

        await db.SaveChangesAsync(ct);

        return await caseService.GetAsync(moderationCase.Id, ct);
    }

    /// <summary>
    /// Фиксации, по которым создаются наблюдения. <paramref name="error"/> не <c>null</c>, если
    /// выбор неоднозначен или фиксация не принадлежит списку источника.
    /// </summary>
    private static List<Guid> ResolveTargets(
        ModerationCase moderationCase,
        ApproveCaseRequest request,
        ProposedData proposed,
        HashSet<Guid> sourceObjectIds,
        int sourceObjectCount,
        bool linkByText,
        out IResult? error)
    {
        error = null;

        var requested = request.MapObjectIds.Distinct().ToList();
        var isObjectNotFound = moderationCase.Kind == ModerationCaseKinds.ObjectNotFound;

        if (isObjectNotFound && requested.Count == 0)
        {
            error = ProblemResponses.Validation(
                new ValidationError(
                    "mapObjectIds",
                    "Для кейса object_not_found выберите фиксацию из списка источника"));

            return [];
        }

        if (requested.Count > 0)
        {
            var outside = requested.FirstOrDefault(item => !sourceObjectIds.Contains(item));
            if (outside != Guid.Empty)
            {
                error = ProblemResponses.Validation(
                    new ValidationError(
                        "mapObjectIds",
                        $"Фиксация {outside} не привязана к источнику"));

                return [];
            }

            return requested;
        }

        var proposedObjectId = moderationCase.MapObjectId ?? proposed.MapObjectId;
        if (proposedObjectId is { } objectId)
        {
            if (!sourceObjectIds.Contains(objectId))
            {
                error = ProblemResponses.Validation(
                    new ValidationError(
                        "mapObjectIds",
                        $"Фиксация {objectId} не привязана к источнику"));

                return [];
            }

            return [objectId];
        }

        if (!linkByText || sourceObjectCount == 1)
        {
            if (sourceObjectIds.Count == 0)
            {
                error = ProblemResponses.Validation(
                    new ValidationError("mapObjectIds", "У источника нет фиксаций"));

                return [];
            }

            return [.. sourceObjectIds];
        }

        error = ProblemResponses.Validation(
            new ValidationError(
                "mapObjectIds",
                "Выберите фиксацию: режим «по тексту» включён, а система не определила объект"));

        return [];
    }

    private static SourceStatusInfo? ResolveStatus(
        ApproveCaseRequest request,
        ProposedData proposed,
        IReadOnlyList<SourceStatusInfo> statuses,
        out IResult? error)
    {
        error = null;

        if (request.SourceStatusId is { } requestedId)
        {
            var requested = statuses.FirstOrDefault(status => status.Id == requestedId);
            if (requested is null)
            {
                error = ProblemResponses.Validation(
                    new ValidationError(
                        "sourceStatusId",
                        "Статус не принадлежит источнику кейса"));

                return null;
            }

            return requested;
        }

        if (proposed.StatusCode is { } code)
        {
            var found = statuses.FirstOrDefault(
                status => string.Equals(status.Code, code, StringComparison.Ordinal));
            if (found is null)
            {
                error = ProblemResponses.Validation(
                    new ValidationError(
                        "sourceStatusId",
                        $"Статус '{code}' не принадлежит источнику"));

                return null;
            }

            return found;
        }

        error = ProblemResponses.Validation(
            new ValidationError(
                "sourceStatusId",
                "Не удалось определить статус: укажите sourceStatusId"));

        return null;
    }

    private static ProposedData ReadProposed(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ProposedData(null, null, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            return new ProposedData(
                ReadString(root, "statusCode"),
                ReadGuid(root, "mapObjectId"),
                ReadFloat(root, "confidence"),
                ReadString(root, "address"));
        }
        catch (JsonException)
        {
            return new ProposedData(null, null, null, null);
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Guid? ReadGuid(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.TryGetGuid(out var id)
            ? id
            : null;

    private static float? ReadFloat(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? (float)number
            : null;

    private static bool TryMapKind(string sourceType, out SourceKind kind)
    {
        switch (sourceType)
        {
            case "telegram_chat":
                kind = SourceKind.Telegram;

                return true;
            case "max_chat":
                kind = SourceKind.Max;

                return true;
            default:
                kind = default;

                return false;
        }
    }

    /// <summary><c>raw_messages.posted_at</c> — время, которое увидит карта после одобрения.</summary>
    private async Task<DateTimeOffset?> ReadPostedAtAsync(Guid rawMessageId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;

        if (closeAfter)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = "select posted_at from raw_messages where id = @id";

            var parameter = command.CreateParameter();
            parameter.ParameterName = "id";
            parameter.Value = rawMessageId;
            command.Parameters.Add(parameter);

            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            return new DateTimeOffset(
                DateTime.SpecifyKind(reader.GetFieldValue<DateTime>(0), DateTimeKind.Utc));
        }
        finally
        {
            if (closeAfter)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    /// <summary>Поля <c>moderation_cases.proposed</c>, нужные для одобрения.</summary>
    private sealed record ProposedData(string? StatusCode, Guid? MapObjectId, float? Confidence, string? Address);
}
