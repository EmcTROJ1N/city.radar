using BuildingBlocks.Messaging;
using BuildingBlocks.Persistence;

using Ingestion.Abstractions;
using Ingestion.Pipeline;

using Microsoft.Extensions.Logging;

namespace IngestionWorker.Host;

/// <summary>
/// Результат обработки одного сообщения источника.
/// </summary>
public enum IngestionOutcome
{
    /// <summary>Сообщение обработано: pipeline отработал, решение записано (если было).</summary>
    Processed,

    /// <summary>Сообщение с таким <c>external_id</c> уже обработано ранее — pipeline не запускался.</summary>
    Duplicate,

    /// <summary>Источник удалён к моменту обработки: сообщение не сохранено.</summary>
    SourceUnavailable,
}

/// <summary>
/// Обработка одного <see cref="RawMessage"/>.
/// </summary>
/// <remarks>
/// Одна транзакция: вставка в <c>raw_messages</c> по <c>(source_id, external_id)</c> → свежий
/// снимок настроек источника → <see cref="MessagePipeline"/> → сообщения outbox для Api.
/// Если запись <c>raw_messages</c> уже существовала, pipeline повторно не запускается.
/// Если запись в outbox падает, откатывается и сохранение сообщения — событие без данных
/// и данные без события недопустимы.
/// </remarks>
internal sealed class MessageIngestionService(
    MayakDbContext db,
    IOutbox outbox,
    MessagePipeline pipeline,
    RawMessageStore rawMessages,
    SourceSnapshotBuilder snapshots,
    ILogger<MessageIngestionService> logger)
{
    /// <summary>Сторона-получатель сообщений Worker-а: решения разбирает Api.</summary>
    public const string ApiDestination = "api";

    /// <summary>Тип сообщения outbox с предложением наблюдения.</summary>
    public const string ObservationProposedType = "ObservationProposed";

    /// <summary>Тип сообщения outbox с запросом карточки модерации.</summary>
    public const string ModerationCaseRequestedType = "ModerationCaseRequested";

    /// <summary>Обрабатывает сообщение источника в одной транзакции с записью в outbox.</summary>
    /// <param name="message">Нормализованное сообщение адаптера.</param>
    /// <param name="ct">Токен отмены.</param>
    public async Task<IngestionOutcome> HandleAsync(RawMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        await using var transaction = await db.Database.BeginTransactionAsync(ct)
            .ConfigureAwait(false);

        var rawMessageId = await rawMessages
            .TryInsertAsync(message, transaction, ct)
            .ConfigureAwait(false);

        if (rawMessageId is null)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);

            logger.LogDebug(
                "Worker: сообщение {ExternalId} источника {SourceId} уже обработано.",
                message.ExternalId,
                message.SourceId);

            return IngestionOutcome.Duplicate;
        }

        var snapshot = await snapshots.BuildAsync(message.SourceId, ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);

            logger.LogWarning(
                "Worker: источник {SourceId} удалён, сообщение {ExternalId} не сохранено.",
                message.SourceId,
                message.ExternalId);

            return IngestionOutcome.SourceUnavailable;
        }

        var decision = await pipeline
            .ProcessAsync(message, rawMessageId.Value, snapshot, ct)
            .ConfigureAwait(false);

        Enqueue(decision);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return IngestionOutcome.Processed;
    }

    /// <summary>Пишет решение pipeline в outbox для Api (без <c>SaveChanges</c> — он в транзакции).</summary>
    private void Enqueue(PipelineDecision decision)
    {
        switch (decision)
        {
            case PipelineDecision.Propose propose:
                foreach (var observation in propose.Observations)
                {
                    outbox.Enqueue(
                        ApiDestination,
                        ObservationProposedType,
                        ObservationProposedPayload.From(observation));
                }

                break;

            case PipelineDecision.Moderate moderate:
                outbox.Enqueue(
                    ApiDestination,
                    ModerationCaseRequestedType,
                    ModerationCaseRequestedPayload.From(moderate.Request));

                break;

            case PipelineDecision.Drop:
            default:
                // Отброс не порождает событий: ни карты, ни очереди модерации.
                break;
        }
    }
}

/// <summary>
/// Payload outbox-сообщения <c>ObservationProposed</c> (формат согласован с Api):
/// имена полей camelCase, время — ISO-8601. Отдельный тип фиксирует контракт и не даёт
/// случайно опубликовать internal-поля pipeline.
/// </summary>
/// <param name="SourceId">Источник сообщения.</param>
/// <param name="MapObjectId">Фиксация, к которой относится статус.</param>
/// <param name="StatusCode">Код статуса из списка источника.</param>
/// <param name="RawMessageId">Запись <c>raw_messages</c>.</param>
/// <param name="ObservedAt">Время публикации сообщения.</param>
/// <param name="Confidence">Уверенность модели, 0..1.</param>
internal sealed record ObservationProposedPayload(
    Guid SourceId,
    Guid MapObjectId,
    string StatusCode,
    Guid RawMessageId,
    DateTimeOffset ObservedAt,
    double Confidence)
{
    public static ObservationProposedPayload From(ObservationProposed observation) => new(
        observation.SourceId,
        observation.MapObjectId,
        observation.StatusCode,
        observation.RawMessageId,
        observation.ObservedAt,
        observation.Confidence);
}

/// <summary>
/// Payload outbox-сообщения <c>ModerationCaseRequested</c>. Поле <c>kind</c> — строка из
/// check-констрейнта <c>moderation_cases.kind</c>, а не значение enum (<see cref="ModerationKind"/>).
/// </summary>
/// <param name="SourceId">Источник сообщения.</param>
/// <param name="RawMessageId">Запись <c>raw_messages</c>.</param>
/// <param name="Kind">Вид карточки: <c>low_confidence</c>, <c>invalid_status</c>, <c>object_not_found</c>, <c>invalid_response</c>.</param>
/// <param name="Reason">Причина для карточки модерации.</param>
/// <param name="ProposedStatusCode">Код статуса из ответа LLM; <c>null</c>, если ответа не было.</param>
/// <param name="ProposedMapObjectId">Фиксация из ответа LLM; <c>null</c>, если не определена.</param>
/// <param name="Confidence">Уверенность модели; <c>null</c>, если ответа не было.</param>
/// <param name="EvidenceQuote">Цитата из ответа; <c>null</c>, если её нет.</param>
internal sealed record ModerationCaseRequestedPayload(
    Guid SourceId,
    Guid RawMessageId,
    string Kind,
    string Reason,
    string? ProposedStatusCode,
    Guid? ProposedMapObjectId,
    double? Confidence,
    string? EvidenceQuote)
{
    public static ModerationCaseRequestedPayload From(ModerationCaseRequested request) => new(
        request.SourceId,
        request.RawMessageId,
        request.Kind.ToDatabaseValue(),
        request.Reason,
        request.ProposedStatusCode,
        request.ProposedMapObjectId,
        request.Confidence,
        request.EvidenceQuote);
}
