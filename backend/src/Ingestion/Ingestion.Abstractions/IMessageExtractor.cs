namespace Ingestion.Abstractions;

/// <summary>
/// Извлекает из сообщения предложение наблюдения: передаёт текст, статусы источника,
/// промпт и фиксации в LLM и возвращает нормализованный результат. Валидацию результата
/// (статус из списка, цитата-подстрока, порог confidence) выполняет pipeline.
/// </summary>
public interface IMessageExtractor
{
    /// <summary>Разбирает сообщение источника.</summary>
    /// <param name="input">Текст сообщения, статусы источника, промпт и параметры линковки.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Извлечённое предложение или причина отказа.</returns>
    Task<ExtractionOutcome> ExtractAsync(ExtractionInput input, CancellationToken ct);
}
