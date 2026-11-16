using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Ingestion.Abstractions;

using Microsoft.Extensions.Logging;

using Sources.Contracts;

namespace Ingestion.Llm.OpenAi;

/// <summary>
/// Провайдер LLM через OpenAI-совместимый <c>chat/completions</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ключ, base URL, модель и прокси берутся из <see cref="ISecretReader"/> на каждый запрос через
/// <see cref="OpenAiOptionsMonitor"/>: смена настроек в админке применяется без перезапуска Worker-а
/// в пределах TTL кэша (30 секунд). Ключ и тело сообщения не логируются никогда — в журнал
/// попадают только коды ответа и тип ошибки.
/// </para>
/// <para>
/// Если прокси задан (<see cref="LlmSettings.ProxyUrl"/>), <see cref="SocketsHttpHandler"/> строится
/// только с ним: прямого подключения нет, а недоступный прокси даёт ошибку, а не fallback.
/// Таймаут запроса — 60 секунд, на 429/5xx — один повтор с небольшой задержкой.
/// </para>
/// </remarks>
public sealed class OpenAiProvider : ILlmProvider, IDisposable
{
    /// <summary>Таймаут одного HTTP-запроса к LLM.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Пауза перед единственным повтором на 429/5xx.</summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly OpenAiOptionsMonitor _options;
    private readonly ILogger<OpenAiProvider> _logger;
    private readonly HttpMessageHandler? _fixedHandler;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _retryDelay;
    private readonly SemaphoreSlim _clientGate = new(1, 1);

    private HttpClient? _client;
    private string? _clientProxyUrl;
    private bool _disposed;

    /// <summary>Создаёт провайдер с реальным сетевым транспортом и настройками по умолчанию.</summary>
    /// <param name="options">Кэш настроек LLM из секретов.</param>
    /// <param name="logger">Журнал: без ключа и без тела сообщений.</param>
    public OpenAiProvider(OpenAiOptionsMonitor options, ILogger<OpenAiProvider> logger)
        : this(options, logger, handler: null, DefaultRequestTimeout, DefaultRetryDelay)
    {
    }

    /// <summary>
    /// Создаёт провайдер с подменённым транспортом; используется тестами, чтобы не ходить в сеть.
    /// </summary>
    /// <param name="options">Кэш настроек LLM из секретов.</param>
    /// <param name="logger">Журнал.</param>
    /// <param name="handler">HTTP-транспорт; <c>null</c> — реальный сокет с учётом прокси.</param>
    /// <param name="requestTimeout">Таймаут запроса.</param>
    /// <param name="retryDelay">Пауза перед повтором.</param>
    public OpenAiProvider(
        OpenAiOptionsMonitor options,
        ILogger<OpenAiProvider> logger,
        HttpMessageHandler? handler,
        TimeSpan requestTimeout,
        TimeSpan retryDelay)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(retryDelay, TimeSpan.Zero);

        _options = options;
        _logger = logger;
        _fixedHandler = handler;
        _requestTimeout = requestTimeout;
        _retryDelay = retryDelay;
    }

    /// <inheritdoc />
    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = await _options.GetAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Настройки LLM не заданы.");

        var endpoint = BuildEndpoint(settings.BaseUrl);
        var client = await GetClientAsync(settings.ProxyUrl, ct).ConfigureAwait(false);
        var body = BuildRequestBody(settings.Model, request);

        for (var attempt = 0; ; attempt++)
        {
            using var response = await SendAsync(client, endpoint, settings.ApiKey, body, ct)
                .ConfigureAwait(false);

            if (attempt == 0 && IsRetryable(response.StatusCode))
            {
                // Пауза перед единственным повтором; тело ответа не читаем и не логируем.
                _logger.LogWarning(
                    "LLM: ответ {StatusCode}, повтор через {Delay} мс.",
                    (int)response.StatusCode,
                    (int)_retryDelay.TotalMilliseconds);

                await Task.Delay(_retryDelay, ct).ConfigureAwait(false);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                // Ни тело запроса, ни тело ответа, ни ключ в журнал не попадают.
                _logger.LogWarning(
                    "LLM: запрос завершился с кодом {StatusCode}.",
                    (int)response.StatusCode);

                throw new HttpRequestException(
                    $"LLM вернул HTTP {(int)response.StatusCode}.");
            }

            var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            return ExtractContent(payload);
        }
    }

    /// <summary>
    /// Возвращает HTTP-клиент для текущего прокси. Клиент пересоздаётся только при смене прокси:
    /// пересоздание на каждый запрос исчерпало бы сокеты.
    /// </summary>
    private async Task<HttpClient> GetClientAsync(string? proxyUrl, CancellationToken ct)
    {
        await _clientGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var proxyKey = proxyUrl ?? string.Empty;
            if (_client is not null && string.Equals(_clientProxyUrl, proxyKey, StringComparison.Ordinal))
            {
                return _client;
            }

            _client?.Dispose();

            var handler = _fixedHandler ?? CreateHandler(proxyUrl);
            _client = new HttpClient(handler, disposeHandler: _fixedHandler is null)
            {
                Timeout = _requestTimeout,
            };
            _clientProxyUrl = proxyKey;

            return _client;
        }
        finally
        {
            _clientGate.Release();
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Uri endpoint,
        string apiKey,
        string body,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        return await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Строит URL метода <c>chat/completions</c> из base URL секрета без двойных слэшей.</summary>
    private static Uri BuildEndpoint(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Base URL LLM задан некорректно.");
        }

        var text = uri.AbsoluteUri.TrimEnd('/');

        return new Uri($"{text}/chat/completions", UriKind.Absolute);
    }

    private static string BuildRequestBody(string model, LlmRequest request)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("Модель LLM не задана.");
        }

        var payload = new
        {
            model,
            temperature = 0,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = request.System },
                new { role = "user", content = request.User + "\n\n" + request.Message },
            },
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>Достаёт текст ответа из <c>choices[0].message.content</c>.</summary>
    private static string ExtractContent(string payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("LLM вернул ответ, который не является JSON.");
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0 ||
                !choices[0].TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("LLM вернул ответ без message.content.");
            }

            return content.GetString() ?? string.Empty;
        }
    }

    private static bool IsRetryable(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    /// <summary>
    /// Строит транспорт. Если прокси задан, используется только он: <c>UseProxy = true</c>
    /// не допускает прямого подключения при недоступном прокси.
    /// </summary>
    private static SocketsHttpHandler CreateHandler(string? proxyUrl)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        if (string.IsNullOrWhiteSpace(proxyUrl))
        {
            handler.UseProxy = false;

            return handler;
        }

        handler.UseProxy = true;
        handler.Proxy = BuildProxy(proxyUrl);

        return handler;
    }

    private static WebProxy BuildProxy(string proxyUrl)
    {
        if (!Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri) ||
            string.IsNullOrEmpty(uri.Host) ||
            uri.Port <= 0)
        {
            throw new InvalidOperationException("Прокси LLM задан некорректно.");
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5"))
        {
            throw new InvalidOperationException(
                $"Прокси LLM со схемой \"{scheme}\" не поддерживается.");
        }

        // Хост и порт без учётных данных: они передаются отдельно через Credentials.
        var proxy = new WebProxy($"{scheme}://{uri.Host}:{uri.Port}")
        {
            BypassProxyOnLocal = false,
        };

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            proxy.Credentials = new NetworkCredential(
                Uri.UnescapeDataString(parts[0]),
                parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
        }

        return proxy;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client?.Dispose();
        _clientGate.Dispose();
    }
}
