namespace Ingestion.Pipeline;

/// <summary>
/// Решение pipeline по одному сообщению: отбросить, опубликовать предложения
/// наблюдений или отправить в очередь модерации. Набор вариантов закрыт.
/// </summary>
public abstract record PipelineDecision
{
    private PipelineDecision()
    {
    }

    /// <summary>Сообщение отброшено и на карту не попадает.</summary>
    /// <param name="Reason">Почему сообщение отброшено (для метрики и журнала).</param>
    public sealed record Drop(DropReason Reason) : PipelineDecision;

    /// <summary>Предложения наблюдений для публикации через outbox (шаг 6).</summary>
    /// <param name="Observations">По одному предложению на каждую фиксацию источника либо одно — для найденной по тексту.</param>
    public sealed record Propose(IReadOnlyList<ObservationProposed> Observations) : PipelineDecision;

    /// <summary>Ответ LLM не прошёл проверки: карточка в очереди модерации, не на карте.</summary>
    /// <param name="Request">Данные карточки модерации.</param>
    public sealed record Moderate(ModerationCaseRequested Request) : PipelineDecision;
}

/// <summary>Причина отброса сообщения без карточки модерации.</summary>
public enum DropReason
{
    /// <summary>Модель вернула <c>relevant = false</c>: сообщение не о состоянии ресурса.</summary>
    NotRelevant,

    /// <summary>Текст не прошёл префильтр источника.</summary>
    PrefilterRejected,

    /// <summary>У источника нет ни одной привязанной фиксации: применять статус некуда.</summary>
    NoLinkedObjects,
}

/// <summary>
/// Предложение наблюдения (шаг 6): направляется в Api через outbox и превращается
/// в <c>Observation</c>. <see cref="ObservedAt"/> — время публикации сообщения.
/// </summary>
/// <param name="SourceId">Источник, из которого получено сообщение.</param>
/// <param name="MapObjectId">Фиксация, к которой относится статус.</param>
/// <param name="StatusCode">Код статуса из списка источника.</param>
/// <param name="RawMessageId">Запись <c>raw_messages</c> с исходным сообщением.</param>
/// <param name="ObservedAt">Время публикации сообщения (UTC).</param>
/// <param name="Confidence">Уверенность модели, 0..1.</param>
public sealed record ObservationProposed(
    Guid SourceId,
    Guid MapObjectId,
    string StatusCode,
    Guid RawMessageId,
    DateTimeOffset ObservedAt,
    double Confidence);

/// <summary>
/// Запрос на карточку модерации. Хранит предложение LLM, чтобы модератор мог
/// принять решение, не перечитывая сообщение модели.
/// </summary>
/// <param name="SourceId">Источник сообщения.</param>
/// <param name="RawMessageId">Запись <c>raw_messages</c> с исходным сообщением.</param>
/// <param name="Kind">Вид проблемы; см. <see cref="ModerationKind"/>.</param>
/// <param name="Reason">Человекочитаемая причина для карточки.</param>
/// <param name="ProposedStatusCode">Код статуса из ответа LLM; <c>null</c>, если ответа не было.</param>
/// <param name="ProposedMapObjectId">Фиксация из ответа LLM; <c>null</c>, если не определена или не является <see cref="Guid"/>.</param>
/// <param name="Confidence">Уверенность модели; <c>null</c>, если ответа не было.</param>
/// <param name="EvidenceQuote">Цитата из ответа; <c>null</c>, если её нет.</param>
public sealed record ModerationCaseRequested(
    Guid SourceId,
    Guid RawMessageId,
    ModerationKind Kind,
    string Reason,
    string? ProposedStatusCode,
    Guid? ProposedMapObjectId,
    double? Confidence,
    string? EvidenceQuote);

/// <summary>
/// Вид карточки модерации, порождённой pipeline. Значения не являются
/// значениями колонки <c>moderation_cases.kind</c> напрямую: схема допускает только
/// <c>low_confidence</c>, <c>invalid_status</c>, <c>object_not_found</c>, <c>invalid_response</c>,
/// <c>anomaly</c> и <c>complaint</c>. Отображение — <see cref="ModerationKindExtensions.ToDatabaseValue"/>.
/// </summary>
public enum ModerationKind
{
    /// <summary>Ответ модели — не JSON ожидаемой схемы.</summary>
    InvalidResponse,

    /// <summary>Код статуса отсутствует в списке статусов этого источника.</summary>
    StatusNotInList,

    /// <summary><c>evidence_quote</c> не является точной подстрокой текста сообщения.</summary>
    EvidenceNotSubstring,

    /// <summary>Фиксация не определена или не входит в список фиксаций источника.</summary>
    ObjectNotFound,

    /// <summary>Confidence ниже порога источника или значения по умолчанию.</summary>
    LowConfidence,

    /// <summary>Вызов LLM завершился ошибкой (сеть, прокси, ответ провайдера).</summary>
    LlmError,
}

/// <summary>
/// Отображение <see cref="ModerationKind"/> на значение колонки <c>moderation_cases.kind</c>.
/// </summary>
/// <remarks>
/// В схеме нет отдельных видов для «цитата не подстрока» и «ошибка LLM», а расширять
/// check-констрейнт ради внутренней диагностики нельзя: всё, что не удалось разобрать или
/// подтвердить, — <c>invalid_response</c>. Точная причина сохраняется в <c>proposed</c>/журнале
/// через <see cref="ModerationCaseRequested.Reason"/>.
/// </remarks>
public static class ModerationKindExtensions
{
    /// <summary>Возвращает значение для колонки <c>moderation_cases.kind</c>.</summary>
    /// <param name="kind">Вид проблемы, найденной pipeline.</param>
    /// <exception cref="ArgumentOutOfRangeException">Значение перечисления неизвестно.</exception>
    public static string ToDatabaseValue(this ModerationKind kind) => kind switch
    {
        ModerationKind.InvalidResponse => "invalid_response",
        ModerationKind.StatusNotInList => "invalid_status",
        ModerationKind.EvidenceNotSubstring => "invalid_response",
        ModerationKind.ObjectNotFound => "object_not_found",
        ModerationKind.LowConfidence => "low_confidence",
        ModerationKind.LlmError => "invalid_response",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный вид карточки модерации."),
    };
}
