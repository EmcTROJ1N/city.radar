using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using BuildingBlocks.Persistence;
using BuildingBlocks.Security;
using BuildingBlocks.Web;

using Microsoft.EntityFrameworkCore;

using Sources.Core.Domain;

namespace Sources.Core.Secrets;

/// <summary>
/// Проверка секретов до сохранения: токен бота проверяется
/// запросом <c>getMe</c>, модель LLM — коротким запросом <c>chat/completions</c>, прокси —
/// TCP-подключением. Ошибки возвращаются как <see cref="ValidationError"/> и показываются
/// в форме; значения секретов не логируются и не попадают в текст ошибки.
/// </summary>
/// <remarks>
/// Проверка не подменяет прямой доступ прокси: если прокси задан, запрос идёт только через
/// него. MTProxy для Bot API/LLM неприменим и возвращает понятную ошибку поля <c>proxyId</c>.
/// </remarks>
internal sealed partial class SecretValidationService(MayakDbContext db, ISecretCipher cipher)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProxyConnectTimeout = TimeSpan.FromSeconds(10);

    private const string TelegramApiBase = "https://api.telegram.org";

    /// <summary>
    /// Проверяет Telegram-секрет с учётом выбранного способа входа. Токен бота проверяется
    /// запросом <c>getMe</c>; JSON-сессия MTProto проверяется по структуре и не обращается
    /// к Bot API. Если способ не задан, он определяется по формату значения.
    /// </summary>
    /// <param name="session">Токен бота или JSON-сессия MTProto.</param>
    /// <param name="mode">Способ входа: <c>bot</c>, <c>userbot</c> или <c>null</c>.</param>
    /// <param name="proxyId">Прокси, назначенный аккаунту; проверка пойдёт только через него.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Ошибка поля или <c>null</c>, если значение принято.</returns>
    public async Task<ValidationError?> ValidateTelegramSessionAsync(
        string session,
        string? mode,
        Guid? proxyId,
        CancellationToken ct)
    {
        var effectiveMode = string.IsNullOrWhiteSpace(mode)
            ? TelegramSessionFormat.Detect(session)
            : mode;

        if (string.Equals(effectiveMode, TelegramSecretModes.Userbot, StringComparison.Ordinal))
        {
            var mtProtoError = TelegramSessionFormat.ValidateMtProto(session);

            return mtProtoError is null ? null : new ValidationError("session", mtProtoError);
        }

        if (TelegramSessionFormat.LooksLikeMtProto(session))
        {
            return new ValidationError(
                "session",
                "Это JSON-сессия MTProto. Выберите способ входа «Аккаунт Telegram (MTProto)».");
        }

        if (!BotTokenPattern().IsMatch(session))
        {
            return new ValidationError(
                "session",
                "Токен должен выглядеть так: 123456789:AAH... Скопируйте его у @BotFather.");
        }

        var (proxy, proxyError) = await ReadProxyAsync(proxyId, ct).ConfigureAwait(false);
        if (proxyError is not null)
        {
            return proxyError;
        }

        SocketsHttpHandler handler;
        try
        {
            handler = CreateHandler(proxy);
        }
        catch (Exception exception) when (exception is InvalidOperationException or UriFormatException or NotSupportedException)
        {
            return new ValidationError("proxyId", exception.Message);
        }

        using (handler)
        using (var client = new HttpClient(handler) { Timeout = HttpTimeout })
        {
            try
            {
                using var response = await client
                    .GetAsync($"{TelegramApiBase}/bot{session}/getMe", ct)
                    .ConfigureAwait(false);

                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (response.IsSuccessStatusCode && IsTelegramOk(body))
                {
                    return null;
                }

                var description = Redact(ReadTelegramDescription(body), session);

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound
                    || description.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
                {
                    return new ValidationError(
                        "session",
                        $"Telegram отклонил токен бота: {description}.");
                }

                return NetworkError(
                    "session",
                    proxy,
                    $"Telegram вернул HTTP {(int)response.StatusCode}: {description}.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return NetworkError("session", proxy, "Telegram не ответил за отведённое время.");
            }
            catch (HttpRequestException exception)
            {
                return NetworkError(
                    "session",
                    proxy,
                    $"Не удалось связаться с Telegram: {Redact(exception.Message, session)}.");
            }
            catch (SocketException exception)
            {
                return NetworkError(
                    "session",
                    proxy,
                    $"Не удалось связаться с Telegram: {exception.SocketErrorCode}.");
            }
        }
    }

    /// <summary>
    /// Проверяет ключ, base URL и модель LLM коротким запросом <c>chat/completions</c>.
    /// Запрос повторяет формат рабочего вызова (temperature 0 и JSON-режим), поэтому ловит
    /// не только неверный ключ, но и неизвестную модель или неподдерживаемый JSON-режим.
    /// </summary>
    /// <param name="apiKey">Ключ API.</param>
    /// <param name="baseUrl">Базовый URL OpenAI-совместимого API.</param>
    /// <param name="model">Идентификатор модели.</param>
    /// <param name="proxyId">Прокси; проверка пойдёт только через него.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Ошибка поля или <c>null</c>, если провайдер принял запрос.</returns>
    public async Task<ValidationError?> ValidateLlmAsync(
        string apiKey,
        string baseUrl,
        string model,
        Guid? proxyId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new ValidationError("apiKey", "Укажите ключ API.");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return new ValidationError("model", "Укажите модель.");
        }

        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            return new ValidationError("baseUrl", "Base URL должен быть абсолютным http(s)-адресом.");
        }

        var endpoint = new Uri($"{baseUri.AbsoluteUri.TrimEnd('/')}/chat/completions");

        var (proxy, proxyError) = await ReadProxyAsync(proxyId, ct).ConfigureAwait(false);
        if (proxyError is not null)
        {
            return proxyError;
        }

        SocketsHttpHandler handler;
        try
        {
            handler = CreateHandler(proxy);
        }
        catch (Exception exception) when (exception is InvalidOperationException or UriFormatException or NotSupportedException)
        {
            return new ValidationError("proxyId", exception.Message);
        }

        var payload = new
        {
            model,
            temperature = 0,
            response_format = new { type = "json_object" },
            messages = new[] { new { role = "user", content = "ping" } },
        };

        using (handler)
        using (var client = new HttpClient(handler) { Timeout = HttpTimeout })
        using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            try
            {
                using var response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var description = Redact(ReadLlmError(body), apiKey);

                if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                {
                    return new ValidationError(
                        "proxyId",
                        "Прокси требует авторизации: проверьте логин и пароль прокси.");
                }

                var field = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "apiKey",
                    HttpStatusCode.TooManyRequests => "baseUrl",
                    _ => "model",
                };

                return new ValidationError(
                    field,
                    $"LLM вернул HTTP {(int)response.StatusCode}: {description}.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return NetworkError("baseUrl", proxy, "LLM не ответил за отведённое время.");
            }
            catch (HttpRequestException exception)
            {
                return NetworkError(
                    "baseUrl",
                    proxy,
                    $"Не удалось связаться с LLM: {Redact(exception.Message, apiKey)}.");
            }
            catch (SocketException exception)
            {
                return NetworkError(
                    "baseUrl",
                    proxy,
                    $"Не удалось связаться с LLM: {exception.SocketErrorCode}.");
            }
        }
    }

    /// <summary>Проверяет доступность прокси TCP-подключением к его хосту и порту.</summary>
    /// <param name="host">Хост прокси.</param>
    /// <param name="port">Порт прокси.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Ошибка поля или <c>null</c>, если TCP-соединение установилось.</returns>
    public async Task<ValidationError?> ValidateProxyAsync(
        string host,
        int port,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return new ValidationError("host", "Укажите хост прокси.");
        }

        if (port is <= 0 or > 65535)
        {
            return new ValidationError("port", "Порт должен быть от 1 до 65535.");
        }

        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProxyConnectTimeout);

            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);

            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ValidationError(
                "host",
                $"Прокси {host}:{port} не ответил за 10 секунд.");
        }
        catch (SocketException exception)
        {
            return new ValidationError(
                "host",
                $"Прокси {host}:{port} недоступен: {exception.SocketErrorCode}.");
        }
    }

    /// <summary>Создаёт транспорт: без прокси — прямое соединение, с прокси — только через него.</summary>
    private static SocketsHttpHandler CreateHandler(ProxyConfig? proxy)
    {
        if (proxy is null)
        {
            return new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectTimeout = ProxyConnectTimeout,
            };
        }

        var scheme = proxy.Scheme.Trim().ToLowerInvariant();
        if (scheme is "mtproxy" or "mtproxysecret")
        {
            throw new InvalidOperationException(
                "MTProxy не применяется к Telegram Bot API и LLM: укажите SOCKS5 или HTTP-прокси. "
                + "MTProxy поддерживается только адаптером MTProto.");
        }

        if (scheme is not ("socks5" or "socks5h" or "socks" or "http" or "https"))
        {
            throw new InvalidOperationException(
                $"Неизвестная схема прокси \"{proxy.Scheme}\": допустимы socks5, http и mtproxy.");
        }

        if (string.IsNullOrWhiteSpace(proxy.Host))
        {
            throw new InvalidOperationException("Не задан хост прокси.");
        }

        if (proxy.Port is <= 0 or > 65535)
        {
            throw new InvalidOperationException($"Некорректный порт прокси: {proxy.Port}.");
        }

        var normalizedScheme = scheme switch
        {
            "https" => "http",
            "socks5h" or "socks" => "socks5",
            _ => scheme,
        };

        var webProxy = new WebProxy($"{normalizedScheme}://{proxy.Host}:{proxy.Port}")
        {
            BypassProxyOnLocal = false,
        };

        if (!string.IsNullOrEmpty(proxy.Username))
        {
            webProxy.Credentials = new NetworkCredential(
                proxy.Username,
                proxy.Password ?? string.Empty);
        }

        return new SocketsHttpHandler
        {
            Proxy = webProxy,
            UseProxy = true,
            ConnectTimeout = ProxyConnectTimeout,
        };
    }

    /// <summary>Читает и расшифровывает параметры прокси; значение наружу не отдаётся.</summary>
    private async Task<(ProxyConfig? Proxy, ValidationError? Error)> ReadProxyAsync(
        Guid? proxyId,
        CancellationToken ct)
    {
        if (proxyId is not { } id)
        {
            return (null, null);
        }

        var secret = await db.Set<Secret>()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id && item.Kind == SecretKinds.Proxy, ct)
            .ConfigureAwait(false);

        if (secret is null)
        {
            return (null, new ValidationError("proxyId", "Прокси не найден."));
        }

        try
        {
            var json = Encoding.UTF8.GetString(cipher.Decrypt(secret.Ciphertext, secret.KeyVersion));
            var payload = JsonSerializer.Deserialize<ProxyPayload>(json, JsonOptions);

            if (payload is null
                || string.IsNullOrWhiteSpace(payload.Scheme)
                || string.IsNullOrWhiteSpace(payload.Host)
                || payload.Port is <= 0 or > 65535)
            {
                return (null, new ValidationError(
                    "proxyId",
                    "Параметры прокси неполны: проверьте схему, хост и порт."));
            }

            return (
                new ProxyConfig(
                    payload.Scheme,
                    payload.Host,
                    payload.Port,
                    payload.Username,
                    payload.Password),
                null);
        }
        catch (Exception exception)
            when (exception is JsonException or CryptographicException or ArgumentException)
        {
            return (null, new ValidationError(
                "proxyId",
                "Не удалось прочитать секрет прокси. Задайте прокси заново."));
        }
    }

    /// <summary>Ошибка сети: при заданном прокси относится к полю <c>proxyId</c>.</summary>
    private static ValidationError NetworkError(
        string field,
        ProxyConfig? proxy,
        string message) =>
        proxy is null
            ? new ValidationError(field, message)
            : new ValidationError(
                "proxyId",
                $"Прокси {proxy.Host}:{proxy.Port} не пропустил соединение: {message}");

    private static bool IsTelegramOk(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            return document.RootElement.TryGetProperty("ok", out var ok)
                && ok.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ReadTelegramDescription(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("description", out var description)
                && description.ValueKind == JsonValueKind.String)
            {
                return description.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Тело не JSON — вернём описание по умолчанию.
        }

        return "без описания";
    }

    private static string ReadLlmError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString() ?? string.Empty;
                }

                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? string.Empty;
                }
            }

            if (root.TryGetProperty("message", out var topLevel)
                && topLevel.ValueKind == JsonValueKind.String)
            {
                return topLevel.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Провайдер вернул не JSON — покажем текст как есть, но обрежем.
        }

        return body;
    }

    /// <summary>Убирает секрет из текста ошибки и ограничивает длину для UI.</summary>
    private static string Redact(string? text, string secret)
    {
        var result = string.IsNullOrWhiteSpace(text) ? "без описания" : text.Trim();

        if (!string.IsNullOrEmpty(secret))
        {
            result = result.Replace(secret, "***", StringComparison.Ordinal);
        }

        return result.Length > 300 ? result[..300] : result;
    }

    [GeneratedRegex(@"^\d{5,}:[A-Za-z0-9_-]{20,}$")]
    private static partial Regex BotTokenPattern();

    private sealed record ProxyConfig(
        string Scheme,
        string Host,
        int Port,
        string? Username,
        string? Password);

    private sealed record ProxyPayload(
        string Scheme,
        string Host,
        int Port,
        string? Username,
        string? Password);
}
