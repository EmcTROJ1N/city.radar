using System.Text.Json;

using Ingestion.Abstractions;

namespace Api.IntegrationTests.Ingestion;

/// <summary>
/// Fake-провайдер LLM для тестов Worker-а: записывает каждый запрос (проверка), позволяет
/// задать ответ по тексту сообщения и при необходимости прочитать текущий ключ LLM из секретов
/// (проверка без перезапуска).
/// </summary>
internal sealed class FakeLlmProvider : ILlmProvider
{
    private readonly object _sync = new();
    private readonly List<LlmRequest> _requests = [];
    private readonly List<string?> _observedApiKeys = [];

    /// <summary>Ответ модели по запросу; по умолчанию — валидный статус «ok».</summary>
    public Func<LlmRequest, string> ResponseFactory { get; set; } =
        _ => JsonResponse(relevant: true, status: "ok", objectId: null, confidence: 0.9, quote: "Вода есть");

    /// <summary>Проверка текущего ключа LLM на каждый запрос; <c>null</c> — не проверять.</summary>
    public Func<CancellationToken, Task<string?>>? ApiKeyProbe { get; set; }

    /// <summary>Исключение, которым должен завершиться вызов LLM (проверка ухода в модерацию).</summary>
    public Exception? Failure { get; set; }

    public int RequestCount
    {
        get
        {
            lock (_sync)
            {
                return _requests.Count;
            }
        }
    }

    public IReadOnlyList<LlmRequest> Requests
    {
        get
        {
            lock (_sync)
            {
                return _requests.ToArray();
            }
        }
    }

    public IReadOnlyList<string?> ObservedApiKeys
    {
        get
        {
            lock (_sync)
            {
                return _observedApiKeys.ToArray();
            }
        }
    }

    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        lock (_sync)
        {
            _requests.Add(request);
        }

        if (ApiKeyProbe is not null)
        {
            var key = await ApiKeyProbe(ct).ConfigureAwait(false);

            lock (_sync)
            {
                _observedApiKeys.Add(key);
            }
        }

        if (Failure is not null)
        {
            throw Failure;
        }

        return ResponseFactory(request);
    }

    /// <summary>Собирает ответ модели в формате схемы.</summary>
    public static string JsonResponse(
        bool relevant,
        string? status = null,
        string? objectId = null,
        double confidence = 0,
        string? quote = null)
    {
        if (!relevant)
        {
            return JsonSerializer.Serialize(new { relevant = false });
        }

        return JsonSerializer.Serialize(new
        {
            relevant = true,
            status,
            object_id = objectId,
            confidence,
            evidence_quote = quote ?? string.Empty,
        });
    }
}
