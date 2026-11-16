namespace Sources.Contracts;

/// <summary>
/// Чтение расшифрованных секретов Worker-ом.
/// Интерфейс намеренно не регистрируется в <c>Api.Host</c>: значения секретов доступны только
/// процессу обработки, а API умеет их лишь заменять. Регистрация — только через
/// <c>AddSecretReader()</c> в composition root Worker-а.
/// </summary>
public interface ISecretReader
{
    /// <summary>
    /// Возвращает расшифрованное значение секрета или <c>null</c>, если секрет не найден.
    /// Для <c>telegram_session</c> — строка сессии, для <c>proxy</c> и <c>llm</c> — JSON.
    /// </summary>
    Task<string?> ReadAsync(Guid secretId, CancellationToken ct);

    /// <summary>
    /// Возвращает настройки LLM из единственного секрета вида <c>llm</c> или <c>null</c>,
    /// если он ещё не задан. <see cref="LlmSettings.ProxyUrl"/> — прокси LLM, если назначен.
    /// </summary>
    Task<LlmSettings?> ReadLlmAsync(CancellationToken ct);
}

/// <summary>
/// Настройки LLM из админки: ключ, base URL и модель применяются Worker-ом
/// без перезапуска. <paramref name="ProxyUrl"/> — прокси из <c>secrets.proxy_id</c> секрета LLM
/// (исходящие запросы к LLM идут через тот же настраиваемый прокси).
/// </summary>
public sealed record LlmSettings(string ApiKey, string BaseUrl, string Model, string? ProxyUrl);
