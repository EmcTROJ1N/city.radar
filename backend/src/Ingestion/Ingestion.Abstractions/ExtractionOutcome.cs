namespace Ingestion.Abstractions;

/// <summary>
/// Закрытый набор результатов разбора сообщения: либо предложение наблюдения,
/// либо отказ с причиной. Набор вариантов закрыт — новые варианты добавляются только здесь.
/// </summary>
public abstract record ExtractionOutcome
{
    private ExtractionOutcome()
    {
    }

    /// <summary>
    /// Извлечённое предложение наблюдения. Ещё не проверено pipeline-ом: статус, цитата,
    /// фиксация и confidence валидируются отдельно.
    /// </summary>
    /// <param name="Status">Код статуса, предложенный LLM.</param>
    /// <param name="ObjectId">Фиксация, выбранная LLM; <c>null</c>, если она не определена.</param>
    /// <param name="Confidence">Уверенность модели в диапазоне 0..1.</param>
    /// <param name="Quote">Цитата из сообщения, обосновывающая статус (<c>evidence_quote</c>).</param>
    public sealed record Extracted(
        string Status,
        Guid? ObjectId,
        double Confidence,
        string Quote) : ExtractionOutcome;

    /// <summary>
    /// Разбор отклонён: сообщение не попадает на карту и направляется в очередь модерации
    /// либо отбрасывается.
    /// </summary>
    /// <param name="Reason">Человекочитаемая причина для журнала и карточки модерации.</param>
    /// <param name="Kind">Машиночитаемый вид отказа.</param>
    public sealed record Rejected(string Reason, RejectionKind Kind) : ExtractionOutcome;
}
