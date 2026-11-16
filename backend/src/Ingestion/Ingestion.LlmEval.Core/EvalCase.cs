namespace Ingestion.LlmEval.Core;

/// <summary>
/// Один размеченный случай набора: текст сообщения и ожидаемое решение
/// человека-разметчика. Один случай соответствует одному сообщению источника.
/// </summary>
/// <param name="Id">Идентификатор случая: из поля <c>id</c> набора либо <c>case-N</c> по номеру строки.</param>
/// <param name="Text">Текст сообщения в том виде, в каком он попал бы в pipeline.</param>
/// <param name="Expected">Ожидаемое решение разметчика.</param>
/// <param name="Note">Комментарий разметчика; <c>null</c>, если его нет.</param>
public sealed record EvalCase(string Id, string Text, EvalExpectation Expected, string? Note);

/// <summary>
/// Ожидаемое решение разметчика. <see cref="Status"/> и <see cref="ObjectCode"/> — коды из
/// конфигурации источника (<c>--sources</c>), а не внутренние идентификаторы.
/// </summary>
/// <param name="Relevant">
/// Относится ли сообщение к задаче промпта-фильтра. Для нерелевантного сообщения
/// <see cref="Status"/> и <see cref="ObjectCode"/> обязаны быть <c>null</c>.
/// </param>
/// <param name="Status">Ожидаемый код статуса; <c>null</c> для нерелевантного или неоднозначного сообщения.</param>
/// <param name="ObjectCode">Ожидаемый код фиксации; <c>null</c>, если фиксация в тексте не определена.</param>
public sealed record EvalExpectation(bool Relevant, string? Status, string? ObjectCode);

/// <summary>Предложение наблюдения в терминах кодов набора (для отчёта).</summary>
/// <param name="StatusCode">Код статуса из предложения pipeline.</param>
/// <param name="ObjectCode">Код фиксации; <c>null</c>, если фиксацию сопоставить не удалось.</param>
public sealed record EvalProposedObservation(string StatusCode, string? ObjectCode);

/// <summary>Вид решения pipeline по случаю набора.</summary>
public enum EvalDecisionKind
{
    /// <summary>Предложения наблюдений опубликованы без модерации.</summary>
    Propose,

    /// <summary>Карточка очереди модерации.</summary>
    Moderate,

    /// <summary>Сообщение отброшено и никуда не попало.</summary>
    Drop,
}

/// <summary>
/// Результат pipeline по одному случаю набора вместе с проверкой ожиданий.
/// </summary>
/// <param name="Case">Исходный размеченный случай.</param>
/// <param name="Decision">Вид решения pipeline.</param>
/// <param name="Proposed">Опубликованные предложения (только для <see cref="EvalDecisionKind.Propose"/>).</param>
/// <param name="ProposedStatusCode">Статус из карточки модерации; <c>null</c>, если его нет.</param>
/// <param name="ProposedObjectCode">Фиксация из карточки модерации; <c>null</c>, если её нет.</param>
/// <param name="ModerationReason">Причина модерации в терминах метрик (<c>low_confidence</c> и т. п.).</param>
/// <param name="DropReason">Причина отброса в терминах метрик (<c>not_relevant</c> и т. п.).</param>
/// <param name="StatusCorrect">Совпал ли предложенный статус с ожидаемым.</param>
/// <param name="ObjectCorrect">Совпала ли предложенная фиксация с ожидаемой.</param>
/// <param name="Correct">Верно ли решение по случаю целиком (см. формулу в README).</param>
/// <param name="PublishedCount">Сколько наблюдений опубликовано без модерации (единица precision).</param>
/// <param name="PublishedCorrectCount">Сколько опубликованных наблюдений верны и по статусу, и по фиксации.</param>
public sealed record EvalCaseResult(
    EvalCase Case,
    EvalDecisionKind Decision,
    IReadOnlyList<EvalProposedObservation> Proposed,
    string? ProposedStatusCode,
    string? ProposedObjectCode,
    string? ModerationReason,
    string? DropReason,
    bool StatusCorrect,
    bool ObjectCorrect,
    bool Correct,
    int PublishedCount,
    int PublishedCorrectCount);

/// <summary>Результат полного прогона набора: случаи и посчитанные метрики.</summary>
/// <param name="Source">Конфигурация источника, на которой выполнялся прогон.</param>
/// <param name="Cases">Результаты по каждому случаю в порядке набора.</param>
/// <param name="Metrics">Метрики /.</param>
public sealed record EvalRunResult(
    EvalSourceConfig Source,
    IReadOnlyList<EvalCaseResult> Cases,
    EvalMetrics Metrics);
