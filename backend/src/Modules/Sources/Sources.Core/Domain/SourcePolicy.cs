namespace Sources.Core.Domain;

/// <summary>
/// Политика источника (таблица <c>source_policies</c>): таймаут актуальности
/// наблюдений, необязательный порог уверенности LLM и необязательный дешёвый префильтр до LLM.
/// Первичный ключ — <see cref="SourceId"/>; запись удаляется каскадом вместе с источником.
/// </summary>
internal sealed class SourcePolicy
{
    public Guid SourceId { get; set; }

    /// <summary>Таймаут актуальности (interval, строго больше нуля).</summary>
    public TimeSpan Timeout { get; set; }

    /// <summary>Порог уверенности LLM (0..1); <c>null</c> — значение по умолчанию из настроек.</summary>
    public float? ConfidenceThreshold { get; set; }

    /// <summary>Дешёвый префильтр до LLM (jsonb): ключевые слова и регулярные выражения.</summary>
    public string? Prefilter { get; set; }
}
