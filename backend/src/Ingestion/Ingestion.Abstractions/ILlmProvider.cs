namespace Ingestion.Abstractions;

/// <summary>
/// Провайдер LLM. Провайдер скрыт за интерфейсом, чтобы заменить OpenAI на другую модель
/// без изменения pipeline. Ключ, base URL, модель и прокси берутся из зашифрованных секретов.
/// </summary>
public interface ILlmProvider
{
    /// <summary>Отправляет запрос и возвращает сырой текст ответа модели.</summary>
    /// <param name="request">Системный промпт, промпт пользователя и текст сообщения.</param>
    /// <param name="ct">Токен отмены запроса.</param>
    Task<string> CompleteAsync(LlmRequest request, CancellationToken ct);
}
