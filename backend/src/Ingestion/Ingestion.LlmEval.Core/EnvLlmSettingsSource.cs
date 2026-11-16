using Ingestion.Llm.OpenAi;

using Sources.Contracts;

namespace Ingestion.LlmEval.Core;

/// <summary>
/// Настройки LLM для оценки из переменных окружения: ключ, base URL, модель и прокси
/// читаются при каждом запросе, как в Worker-е, но без базы и админки.
/// </summary>
/// <remarks>
/// Ключ никогда не выводится и не попадает в отчёт. Если ключ не задан, возвращается <c>null</c>:
/// провайдер сообщит «Настройки LLM не заданы», не раскрывая окружение.
/// </remarks>
public sealed class EnvLlmSettingsSource : ILlmSettingsSource
{
    /// <summary>Переменная с API-ключом LLM.</summary>
    public const string ApiKeyVariable = "MAYAK_LLM_API_KEY";

    /// <summary>Переменная с base URL OpenAI-совместимого API.</summary>
    public const string BaseUrlVariable = "MAYAK_LLM_BASE_URL";

    /// <summary>Переменная с именем модели.</summary>
    public const string ModelVariable = "MAYAK_LLM_MODEL";

    /// <summary>Переменная с URL прокси; пустая — без прокси.</summary>
    public const string ProxyVariable = "MAYAK_LLM_PROXY";

    /// <summary>Base URL по умолчанию, если переменная не задана.</summary>
    public const string DefaultBaseUrl = "https://api.openai.com/v1";

    /// <inheritdoc />
    public Task<LlmSettings?> ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var apiKey = Environment.GetEnvironmentVariable(ApiKeyVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Task.FromResult<LlmSettings?>(null);
        }

        var baseUrl = Environment.GetEnvironmentVariable(BaseUrlVariable);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = DefaultBaseUrl;
        }

        var model = Environment.GetEnvironmentVariable(ModelVariable)?.Trim() ?? string.Empty;
        var proxy = Environment.GetEnvironmentVariable(ProxyVariable)?.Trim();

        return Task.FromResult<LlmSettings?>(new LlmSettings(
            apiKey.Trim(),
            baseUrl.Trim(),
            model,
            string.IsNullOrEmpty(proxy) ? null : proxy));
    }
}
