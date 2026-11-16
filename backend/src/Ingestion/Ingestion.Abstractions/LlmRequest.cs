namespace Ingestion.Abstractions;

/// <summary>
/// Запрос к LLM: системный промпт, промпт-фильтр пользователя и текст разбираемого
/// сообщения. Текст сообщения — недоверенные данные, а не инструкции для модели.
/// </summary>
/// <param name="System">Системный промпт с контекстом задачи, статусами источника и форматом ответа.</param>
/// <param name="User">Промпт-фильтр пользователя из настроек источника.</param>
/// <param name="Message">Текст сообщения источника.</param>
public sealed record LlmRequest(string System, string User, string Message);
