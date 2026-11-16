namespace Ingestion.Abstractions;

/// <summary>
/// Причина, по которой предложение наблюдения не может попасть на карту.
/// Используется для маршрутизации в очередь модерации и для диагностики.
/// </summary>
public enum RejectionKind
{
    /// <summary>Ответ LLM не является корректным JSON ожидаемой схемы.</summary>
    InvalidResponse,

    /// <summary>Код статуса отсутствует в списке статусов этого источника.</summary>
    StatusNotInList,

    /// <summary>Цитата <c>evidence_quote</c> не является подстрокой исходного сообщения.</summary>
    EvidenceNotSubstring,

    /// <summary>Фиксация не определена или не входит в список фиксаций источника.</summary>
    ObjectNotFound,

    /// <summary>Confidence ниже порога источника или значения по умолчанию.</summary>
    LowConfidence,

    /// <summary>Ошибка вызова LLM (таймаут, сеть, ответ провайдера).</summary>
    LlmError,

    /// <summary>Сообщение не относится к теме промпта (<c>relevant: false</c>).</summary>
    NotRelevant,
}
