namespace Ingestion.Abstractions;

/// <summary>
/// Результат запроса к LLM с необязательной информацией о расходе токенов. Контракт
/// <see cref="ILlmProvider"/> возвращает только текст; этот тип предназначен для реализаций
/// и диагностики, которым нужен usage.
/// </summary>
/// <param name="Text">Сырой текст ответа модели.</param>
/// <param name="Usage">Расход токенов; <c>null</c>, если провайдер его не сообщает.</param>
public sealed record LlmResult(string Text, LlmUsage? Usage = null);

/// <summary>Минимальная информация о расходе токенов.</summary>
/// <param name="PromptTokens">Количество токенов запроса.</param>
/// <param name="CompletionTokens">Количество токенов ответа.</param>
public sealed record LlmUsage(int PromptTokens, int CompletionTokens);
