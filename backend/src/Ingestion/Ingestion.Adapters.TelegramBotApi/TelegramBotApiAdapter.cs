using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;

using Ingestion.Abstractions;

namespace Ingestion.Adapters.TelegramBotApi;

/// <summary>
/// Адаптер Telegram Bot API. Читает обновления собственного чата проекта
/// long polling-ом <c>getUpdates</c>: только текст (или подпись к медиа) из чата источника,
/// без сообщений самого бота. Соединение идёт только через прокси, если он задан.
/// </summary>
/// <remarks>
/// <para>
/// Offset обновлений хранится в памяти адаптера: после перезапуска источник дочитает то, что
/// Telegram ещё отдаёт в <c>getUpdates</c>, а дедупликация по <c>external_id</c> в окне памяти
/// и уникальность <c>raw_messages (source_id, external_id)</c> не дают обработать сообщение дважды.
/// </para>
/// <para>
/// Ошибки: 401/404 — <see cref="SourceState.AuthError"/> и остановка опроса (переподключения
/// бессмысленны); 429 — <see cref="SourceState.FloodWait"/> с ожиданием <c>retry_after</c>;
/// сеть/прокси — <see cref="SourceState.Error"/> с экспоненциальным backoff до 5 минут,
/// прямой fallback запрещён.
/// </para>
/// </remarks>
public sealed class TelegramBotApiAdapter : IDataSource
{
    /// <summary>Базовый адрес Telegram Bot API.</summary>
    public const string DefaultApiBaseUrl = "https://api.telegram.org";

    private readonly SourceRuntimeConfig _config;
    private readonly TimeProvider _timeProvider;
    private readonly string _apiBaseUrl;
    private readonly int _pollTimeoutSeconds;
    private readonly ExternalIdDeduper _deduper;
    private readonly object _sync = new();

    private Channel<RawMessage>? _channel;
    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private HttpClient? _httpClient;
    private bool _ownsHttpClient;
    private TelegramChatMatcher? _matcher;
    private long _offset;
    private SourceState _state = SourceState.Unknown;
    private string? _detail;
    private DateTimeOffset? _lastMessageAt;
    private DateTimeOffset? _lastReportedMessageAt;
    private bool _disposed;

    /// <summary>Создаёт адаптер Bot API.</summary>
    /// <param name="config">Снимок источника: токен бота в <see cref="SourceRuntimeConfig.SessionSecret"/>.</param>
    /// <param name="httpClient">
    /// Готовый клиент для тестов на подменённом транспорте; <c>null</c> — клиент с прокси
    /// из конфигурации создаётся адаптером.
    /// </param>
    /// <param name="timeProvider">Часы и таймеры (в тестах — управляемые).</param>
    /// <param name="apiBaseUrl">Базовый адрес Bot API (в тестах — локальный).</param>
    /// <param name="pollTimeoutSeconds">Таймаут long polling, сек.</param>
    /// <param name="dedupWindow">Размер окна дедупликации <c>external_id</c>.</param>
    public TelegramBotApiAdapter(
        SourceRuntimeConfig config,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null,
        string apiBaseUrl = DefaultApiBaseUrl,
        int pollTimeoutSeconds = 25,
        int dedupWindow = 2000)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pollTimeoutSeconds);

        _config = config;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _apiBaseUrl = apiBaseUrl.TrimEnd('/');
        _pollTimeoutSeconds = pollTimeoutSeconds;
        _deduper = new ExternalIdDeduper(dedupWindow);
        _httpClient = httpClient;
        _ownsHttpClient = httpClient is null;
    }

    /// <inheritdoc />
    public Guid SourceId => _config.SourceId;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_loop is not null)
        {
            return Task.CompletedTask;
        }

        _channel = Channel.CreateUnbounded<RawMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        _cancellation = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cancellation.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct)
    {
        if (_cancellation is not null)
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Остановка хоста не должна ждать адаптер бесконечно: поток уже отменён.
            }
            catch (Exception)
            {
                // Ошибка адаптера уже сообщена в health; остановка её не пробрасывает.
            }
        }

        _channel?.Writer.TryComplete();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RawMessage> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = _channel
            ?? throw new InvalidOperationException("Адаптер не запущен: вызовите StartAsync.");

        await foreach (var message in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return message;
        }
    }

    /// <inheritdoc />
    public Task<SourceHealth> CheckHealthAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            return Task.FromResult(new SourceHealth(_state, _detail, _lastMessageAt));
        }
    }

    /// <summary>Основной цикл long polling-а: ошибки не роняют Worker, а меняют health источника.</summary>
    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = new SourceRetryBackoff();

        // Терминальное состояние (auth_error/ошибка конфигурации): поток не завершается,
        // чтобы supervisor не перезапускал адаптер по кругу. Перезапуск произойдёт только
        // после изменения настроек источника (сменится хеш конфигурации).
        var terminal = false;

        try
        {
            if (_config.ProxyRequired && _config.Proxy is null)
            {
                await ReportAsync(
                    SourceState.Error,
                    "Прокси задан в настройках, но его параметры не разрешились: прямое соединение запрещено.",
                    lastMessageAt: null,
                    ct).ConfigureAwait(false);

                terminal = true;

                return;
            }

            if (!TelegramSourceSettings.TryGetChatId(_config, out var chatId, out var chatError))
            {
                await ReportAsync(SourceState.Error, chatError, lastMessageAt: null, ct)
                    .ConfigureAwait(false);
                terminal = true;

                return;
            }

            _matcher = new TelegramChatMatcher(chatId);

            if (string.IsNullOrWhiteSpace(_config.SessionSecret))
            {
                await ReportAsync(
                        SourceState.AuthError,
                        "Не задан токен бота: назначьте Telegram-сессию вида bot-токена.",
                        lastMessageAt: null,
                        ct)
                    .ConfigureAwait(false);
                terminal = true;

                return;
            }

            if (_httpClient is null)
            {
                try
                {
                    var handler = ProxyHttpHandlerFactory.Create(
                        _config.Proxy,
                        _config.ProxyRequired);

                    _httpClient = new HttpClient(handler)
                    {
                        Timeout = Timeout.InfiniteTimeSpan,
                    };
                    _ownsHttpClient = true;
                }
                catch (ProxyConfigurationException exception)
                {
                    await ReportAsync(SourceState.Error, exception.Message, lastMessageAt: null, ct)
                        .ConfigureAwait(false);
                    terminal = true;

                    return;
                }
            }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var hasMessages = await PollOnceAsync(ct).ConfigureAwait(false);

                    backoff.Reset();

                    await ReportAsync(
                            SourceState.Ok,
                            detail: null,
                            hasMessages ? LastMessageAt : null,
                            ct)
                        .ConfigureAwait(false);
                }
                catch (BotApiAuthException exception)
                {
                    // Токен недействителен: переподключения не помогут, адаптер остаётся
                    // в auth_error до изменения настроек (supervisor пересоздаст адаптер).
                    await ReportAsync(
                            SourceState.AuthError,
                            exception.Message,
                            lastMessageAt: null,
                            ct)
                        .ConfigureAwait(false);
                    terminal = true;

                    return;
                }
                catch (BotApiFloodWaitException exception)
                {
                    var until = _timeProvider.GetUtcNow() + exception.RetryAfter;

                    await ReportAsync(
                            SourceState.FloodWait,
                            until.ToString("O", CultureInfo.InvariantCulture),
                            lastMessageAt: null,
                            ct)
                        .ConfigureAwait(false);

                    // Ожидание retry_after плюс собственная пауза: повтор не должен бить
                    // в Telegram сразу после истечения срока.
                    await DelayAsync(exception.RetryAfter + backoff.Next(), ct).ConfigureAwait(false);
                }
                catch (ProxyUnavailableException exception)
                {
                    await ReportAsync(
                            SourceState.Error,
                            exception.Message,
                            lastMessageAt: null,
                            ct)
                        .ConfigureAwait(false);

                    await DelayAsync(backoff.Next(), ct).ConfigureAwait(false);
                }
                catch (HttpRequestException exception)
                {
                    await ReportAsync(
                            SourceState.Error,
                            SafeMessage(exception),
                            lastMessageAt: null,
                            ct)
                        .ConfigureAwait(false);

                    await DelayAsync(backoff.Next(), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    await ReportAsync(
                            SourceState.Error,
                            SafeMessage(exception),
                            lastMessageAt: null,
                            ct)
                        .ConfigureAwait(false);

                    await DelayAsync(backoff.Next(), ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (!terminal)
            {
                _channel?.Writer.TryComplete();
            }
        }
    }

    private DateTimeOffset? LastMessageAt
    {
        get
        {
            lock (_sync)
            {
                return _lastMessageAt;
            }
        }
    }

    /// <summary>Один запрос <c>getUpdates</c> и передача новых сообщений в поток.</summary>
    private async Task<bool> PollOnceAsync(CancellationToken ct)
    {
        var updates = await GetUpdatesAsync(ct).ConfigureAwait(false);
        var hasMessages = false;

        foreach (var update in updates.EnumerateArray())
        {
            if (update.TryGetProperty("update_id", out var updateId) &&
                updateId.TryGetInt64(out var id))
            {
                // Offset сдвигается и для отфильтрованных обновлений: они уже получены.
                _offset = Math.Max(_offset, id + 1);
            }

            if (!update.TryGetProperty("message", out var message))
            {
                // Редактирования, служебные обновления и прочее не являются новыми сообщениями.
                continue;
            }

            var raw = MapMessage(message);
            if (raw is null)
            {
                continue;
            }

            hasMessages = true;

            lock (_sync)
            {
                _lastMessageAt = raw.PostedAt;
            }

            await _channel!.Writer.WriteAsync(raw, ct).ConfigureAwait(false);
        }

        // Подменённый транспорт отвечает мгновенно; пауза не даёт крутиться в tight loop.
        if (!hasMessages && _httpClient is not null)
        {
            await DelayAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }

        return hasMessages;
    }

    /// <summary>
    /// Преобразует сообщение Telegram в <see cref="RawMessage"/>. <c>null</c> — сообщение
    /// не из чата источника, без текста, от бота или уже обработано (дедупликация).
    /// </summary>
    private RawMessage? MapMessage(JsonElement message)
    {
        var matcher = _matcher;
        if (matcher is null || !message.TryGetProperty("chat", out var chat))
        {
            return null;
        }

        if (!chat.TryGetProperty("id", out var chatIdElement) ||
            !chatIdElement.TryGetInt64(out var chatId))
        {
            return null;
        }

        var username = chat.TryGetProperty("username", out var usernameElement) &&
                       usernameElement.ValueKind == JsonValueKind.String
            ? usernameElement.GetString()
            : null;

        // Только сообщения разрешённого чата источника.
        if (!matcher.Matches(username, chatId))
        {
            return null;
        }

        // Сообщения самого бота не читаем: у них from.is_bot = true.
        if (message.TryGetProperty("from", out var from) &&
            from.ValueKind == JsonValueKind.Object &&
            from.TryGetProperty("is_bot", out var isBot) &&
            isBot.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        var text = ReadText(message);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!message.TryGetProperty("message_id", out var messageIdElement) ||
            !messageIdElement.TryGetInt64(out var messageId))
        {
            return null;
        }

        var externalId = $"{matcher.NormalizedChatId}:{messageId.ToString(CultureInfo.InvariantCulture)}";
        if (!_deduper.TryAdd(externalId))
        {
            return null;
        }

        var author = ReadAuthor(from);
        var chatTitle = chat.TryGetProperty("title", out var title) &&
                        title.ValueKind == JsonValueKind.String
            ? title.GetString()
            : username ?? chatId.ToString(CultureInfo.InvariantCulture);

        var link = string.IsNullOrEmpty(username)
            ? null
            : $"https://t.me/{username}/{messageId.ToString(CultureInfo.InvariantCulture)}";

        var postedAt = message.TryGetProperty("date", out var date) && date.TryGetInt64(out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : _timeProvider.GetUtcNow();

        return new RawMessage(
            _config.SourceId,
            externalId,
            text,
            author,
            chatTitle,
            link,
            postedAt.ToUniversalTime());
    }

    private static string? ReadText(JsonElement message)
    {
        if (message.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString();
        }

        if (message.TryGetProperty("caption", out var caption) &&
            caption.ValueKind == JsonValueKind.String)
        {
            return caption.GetString();
        }

        return null;
    }

    private static string? ReadAuthor(JsonElement from)
    {
        if (from.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (from.TryGetProperty("username", out var username) &&
            username.ValueKind == JsonValueKind.String)
        {
            return username.GetString();
        }

        return from.TryGetProperty("id", out var id) && id.TryGetInt64(out var value)
            ? value.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private async Task<JsonElement> GetUpdatesAsync(CancellationToken ct)
    {
        var token = _config.SessionSecret!;
        var url = $"{_apiBaseUrl}/bot{token}/getUpdates" +
                  $"?timeout={_pollTimeoutSeconds.ToString(CultureInfo.InvariantCulture)}" +
                  "&allowed_updates=%5B%22message%22%5D";

        if (_offset > 0)
        {
            url += $"&offset={_offset.ToString(CultureInfo.InvariantCulture)}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _httpClient!
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
        {
            throw new BotApiAuthException(
                "Токен бота недействителен или бот не найден (HTTP 401/404): " +
                "проверьте Telegram-сессию источника.");
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new BotApiFloodWaitException(await ReadRetryAfterAsync(response, ct).ConfigureAwait(false));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Telegram Bot API вернул HTTP {(int)response.StatusCode}: {response.ReasonPhrase}.");
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
        {
            var description = root.TryGetProperty("description", out var text) &&
                              text.ValueKind == JsonValueKind.String
                ? text.GetString() ?? string.Empty
                : "без описания";

            var errorCode = root.TryGetProperty("error_code", out var code) &&
                            code.TryGetInt32(out var value)
                ? value
                : 0;

            // Telegram может вернуть 429 в теле ответа с HTTP 200: это тот же FloodWait.
            if (errorCode == 429)
            {
                var seconds = root.TryGetProperty("parameters", out var parameters) &&
                              parameters.TryGetProperty("retry_after", out var retryAfter) &&
                              retryAfter.TryGetDouble(out var delay)
                    ? delay
                    : 5;

                throw new BotApiFloodWaitException(
                    TimeSpan.FromSeconds(seconds > 0 ? seconds : 5));
            }

            if (errorCode is 401 or 403 or 404 ||
                description.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                throw new BotApiAuthException($"Telegram отклонил токен бота: {description}.");
            }

            throw new HttpRequestException($"Telegram Bot API вернул ошибку: {description}.");
        }

        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
        {
            return result.Clone();
        }

        using (var empty = JsonDocument.Parse("[]"))
        {
            return empty.RootElement.Clone();
        }
    }

    private static async Task<TimeSpan> ReadRetryAfterAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var seconds = response.Headers.RetryAfter?.Delta?.TotalSeconds;

        if (seconds is null)
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var document = JsonDocument.Parse(body);

                if (document.RootElement.TryGetProperty("parameters", out var parameters) &&
                    parameters.TryGetProperty("retry_after", out var retryAfter) &&
                    retryAfter.TryGetDouble(out var value))
                {
                    seconds = value;
                }
            }
            catch (JsonException)
            {
                // Тело не JSON — используем значение по умолчанию.
            }
        }

        var delay = seconds is > 0 ? seconds.Value : 5;

        return TimeSpan.FromSeconds(delay);
    }

    /// <summary>Пишет health; повтор одного и того же состояния не дублируется.</summary>
    private async Task ReportAsync(
        SourceState state,
        string? detail,
        DateTimeOffset? lastMessageAt,
        CancellationToken ct)
    {
        lock (_sync)
        {
            if (lastMessageAt is not null)
            {
                _lastMessageAt = lastMessageAt;
            }

            var changed = _state != state ||
                          !string.Equals(_detail, detail, StringComparison.Ordinal) ||
                          (lastMessageAt is not null && _lastReportedMessageAt != lastMessageAt);

            if (!changed)
            {
                return;
            }

            _state = state;
            _detail = detail;
            if (lastMessageAt is not null)
            {
                _lastReportedMessageAt = lastMessageAt;
            }
        }

        if (_config.ReportHealth is null)
        {
            return;
        }

        try
        {
            await _config.ReportHealth(state, detail, lastMessageAt, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Запись health не должна останавливать чтение источника.
        }
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
    }

    /// <summary>Сообщение об ошибке без токена бота (URL Bot API содержит токен).</summary>
    private string SafeMessage(Exception exception)
    {
        var token = _config.SessionSecret;
        var message = exception.Message;

        if (!string.IsNullOrEmpty(token))
        {
            message = message.Replace(token, "***", StringComparison.Ordinal);
        }

        var type = exception.GetType().Name;

        return message.Contains(type, StringComparison.Ordinal)
            ? message
            : $"{type}: {message}";
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await StopAsync(CancellationToken.None).ConfigureAwait(false);

        _cancellation?.Dispose();

        if (_ownsHttpClient)
        {
            _httpClient?.Dispose();
        }
    }

    private sealed class BotApiAuthException(string message) : Exception(message);

    private sealed class BotApiFloodWaitException(TimeSpan retryAfter) : Exception
    {
        public TimeSpan RetryAfter { get; } = retryAfter;
    }
}

/// <summary>
/// Фабрика адаптера Telegram Bot API. Обслуживает только источники
/// <c>telegram_chat</c> с <c>settings.mode = "bot"</c>; остальные режимы — юзербот MTProto.
/// </summary>
public sealed class TelegramBotApiAdapterFactory(TimeProvider timeProvider) : ISourceAdapterFactory
{
    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <inheritdoc />
    public string SourceType => TelegramSourceSettings.SourceType;

    /// <inheritdoc />
    public bool CanHandle(SourceRuntimeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return string.Equals(config.SourceType, SourceType, StringComparison.Ordinal) &&
               TelegramSourceSettings.IsBotMode(config);
    }

    /// <inheritdoc />
    public IDataSource Create(SourceRuntimeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!CanHandle(config))
        {
            throw new NotSupportedException(
                "Telegram Bot API обслуживает только источник telegram_chat с mode = \"bot\".");
        }

        return new TelegramBotApiAdapter(config, timeProvider: _timeProvider);
    }
}
