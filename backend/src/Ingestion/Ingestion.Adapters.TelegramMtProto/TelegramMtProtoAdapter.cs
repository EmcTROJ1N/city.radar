using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;

using Ingestion.Abstractions;

namespace Ingestion.Adapters.TelegramMtProto;

/// <summary>
/// Адаптер Telegram MTProto (юзербот, WTelegramClient) для чтения сторонних групп
///). Работает только через <see cref="ITelegramClientTransport"/>: в тестах транспорт
/// подменяется заглушкой, реальный Telegram не используется.
/// </summary>
/// <remarks>
/// <para>
/// Секрет сессии — JSON <c>{ apiId, apiHash, sessionData?, phone? }</c>, расшифрованный
/// Worker-ом. Обновлённая сессия передаётся в <see cref="IMtProtoSessionStore"/>.
/// </para>
/// <para>
/// FloodWait — ожидание <c>retry_after</c> и health <see cref="SourceState.FloodWait"/>;
/// бан/разлогин — <see cref="SourceState.AuthError"/> без цикла переподключений; сеть и прокси —
/// <see cref="SourceState.Error"/> с экспоненциальным backoff до 5 минут.
/// </para>
/// </remarks>
public sealed class TelegramMtProtoAdapter : IDataSource
{
    private readonly SourceRuntimeConfig _config;
    private readonly ITelegramClientTransportFactory _transportFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ExternalIdDeduper _deduper;
    private readonly object _sync = new();

    private Channel<RawMessage>? _channel;
    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private TelegramChatMatcher? _matcher;
    private SourceState _state = SourceState.Unknown;
    private string? _detail;
    private DateTimeOffset? _lastMessageAt;
    private DateTimeOffset? _lastReportedMessageAt;
    private bool _disposed;

    /// <summary>Создаёт адаптер MTProto.</summary>
    /// <param name="config">Снимок источника: JSON сессии в <see cref="SourceRuntimeConfig.SessionSecret"/>.</param>
    /// <param name="transportFactory">Фабрика транспорта; в тестах — заглушка.</param>
    /// <param name="timeProvider">Часы и таймеры (в тестах — управляемые).</param>
    /// <param name="dedupWindow">Размер окна дедупликации <c>external_id</c>.</param>
    public TelegramMtProtoAdapter(
        SourceRuntimeConfig config,
        ITelegramClientTransportFactory transportFactory,
        TimeProvider? timeProvider = null,
        int dedupWindow = 2000)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(transportFactory);

        _config = config;
        _transportFactory = transportFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _deduper = new ExternalIdDeduper(dedupWindow);
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
                // Остановка хоста не должна ждать адаптер бесконечно.
            }
            catch (Exception)
            {
                // Ошибка уже сообщена в health; остановка её не пробрасывает.
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

    private async Task RunAsync(CancellationToken ct)
    {
        // Терминальное состояние (auth_error/ошибка конфигурации): поток не завершается, чтобы
        // supervisor не перезапускал адаптер по кругу. Перезапуск — только после смены настроек.
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

            if (!TryReadSession(out var session, out var sessionError))
            {
                await ReportAsync(SourceState.Error, sessionError, lastMessageAt: null, ct)
                    .ConfigureAwait(false);
                terminal = true;

                return;
            }

            var transportConfig = new MtProtoTransportConfig(
                _config.SourceId,
                session.ApiId,
                session.ApiHash,
                session.SessionData,
                session.Phone,
                _config.Proxy);

            var backoff = new SourceRetryBackoff();

            while (!ct.IsCancellationRequested)
            {
                ITelegramClientTransport? transport = null;
                try
                {
                    transport = _transportFactory.Create();
                    await transport.OpenAsync(transportConfig, _matcher.NormalizedChatId, ct)
                        .ConfigureAwait(false);

                    backoff.Reset();

                    await ReportAsync(SourceState.Ok, detail: null, lastMessageAt: null, ct)
                        .ConfigureAwait(false);

                    await foreach (var message in transport.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var raw = Map(message);
                        if (raw is null)
                        {
                            continue;
                        }

                        await ReportAsync(
                                SourceState.Ok,
                                detail: null,
                                raw.PostedAt,
                                ct)
                            .ConfigureAwait(false);

                        await _channel!.Writer.WriteAsync(raw, ct).ConfigureAwait(false);
                    }

                    // Поток завершился без отмены — транспорт потерял соединение.
                    throw new TelegramTransportException(
                        TelegramFailureKind.Network,
                        "Соединение MTProto завершилось: требуется переподключение.");
                }
                catch (TelegramTransportException exception)
                {
                    switch (exception.Kind)
                    {
                        case TelegramFailureKind.Auth:
                            // Разлогин или бан: переподключения не помогут, пока админ не заменит
                            // сессию. Адаптер остаётся в auth_error (supervisor его не перезапускает).
                            await ReportAsync(
                                    SourceState.AuthError,
                                    exception.Message,
                                    lastMessageAt: null,
                                    ct)
                                .ConfigureAwait(false);
                            terminal = true;

                            return;

                        case TelegramFailureKind.Configuration:
                            await ReportAsync(
                                    SourceState.Error,
                                    exception.Message,
                                    lastMessageAt: null,
                                    ct)
                                .ConfigureAwait(false);
                            terminal = true;

                            return;

                        case TelegramFailureKind.FloodWait:
                            var retryAfter = exception.RetryAfter ?? TimeSpan.FromSeconds(5);
                            var until = _timeProvider.GetUtcNow() + retryAfter;

                            await ReportAsync(
                                    SourceState.FloodWait,
                                    until.ToString("O", CultureInfo.InvariantCulture),
                                    lastMessageAt: null,
                                    ct)
                                .ConfigureAwait(false);

                            // Ожидание retry_after плюс собственная пауза перед повтором.
                            await DelayAsync(retryAfter + backoff.Next(), ct).ConfigureAwait(false);
                            break;

                        default:
                            await ReportAsync(
                                    SourceState.Error,
                                    exception.Message,
                                    lastMessageAt: null,
                                    ct)
                                .ConfigureAwait(false);

                            await DelayAsync(backoff.Next(), ct).ConfigureAwait(false);
                            break;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    await ReportAsync(
                            SourceState.Error,
                            $"Ошибка MTProto: {exception.Message}",
                            lastMessageAt: null,
                            ct)
                        .ConfigureAwait(false);

                    await DelayAsync(backoff.Next(), ct).ConfigureAwait(false);
                }
                finally
                {
                    if (transport is not null)
                    {
                        try
                        {
                            await transport.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // Освобождение транспорта не должно мешать переподключению.
                        }
                    }
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

    /// <summary>Разбирает JSON секрета сессии; ошибка — понятная администратору.</summary>
    private bool TryReadSession(out MtProtoSession session, out string? error)
    {
        session = default;

        if (string.IsNullOrWhiteSpace(_config.SessionSecret))
        {
            error = "Не задана Telegram-сессия: ожидается JSON {apiId, apiHash, sessionData?, phone?}.";

            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(_config.SessionSecret);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Telegram-сессия должна быть JSON-объектом {apiId, apiHash, sessionData?, phone?}.";

                return false;
            }

            if (!TryReadApiId(root, out var apiId))
            {
                error = "В Telegram-сессии не задан числовой apiId.";

                return false;
            }

            var apiHash = ReadString(root, "apiHash") ?? ReadString(root, "api_hash");
            if (string.IsNullOrWhiteSpace(apiHash))
            {
                error = "В Telegram-сессии не задан apiHash.";

                return false;
            }

            var sessionData = ReadString(root, "sessionData") ?? ReadString(root, "session_data");
            if (string.IsNullOrWhiteSpace(sessionData))
            {
                // Первичный вход (код из SMS/2FA) в Worker-е не выполняется: без сохранённой
                // сессии источник остаётся в auth_error до назначения сессии админом.
                error = "В Telegram-сессии нет sessionData: выполните вход в аккаунт " +
                        "и назначьте сохранённую сессию источнику.";

                return false;
            }

            session = new MtProtoSession(apiId, apiHash, sessionData, ReadString(root, "phone"));

            error = null;

            return true;
        }
        catch (JsonException)
        {
            error = "Telegram-сессия должна быть JSON {apiId, apiHash, sessionData?, phone?}.";

            return false;
        }
    }

    private static bool TryReadApiId(JsonElement root, out int apiId)
    {
        apiId = 0;

        if (!root.TryGetProperty("apiId", out var value) &&
            !root.TryGetProperty("api_id", out value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out apiId),
            JsonValueKind.String => int.TryParse(
                value.GetString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out apiId),
            _ => false,
        };
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Преобразует сообщение транспорта; <c>null</c> — пустой текст или дубль.</summary>
    private RawMessage? Map(MtProtoIncomingMessage message)
    {
        var matcher = _matcher;
        if (matcher is null || string.IsNullOrWhiteSpace(message.Text))
        {
            return null;
        }

        var externalId =
            $"{matcher.NormalizedChatId}:{message.MessageId.ToString(CultureInfo.InvariantCulture)}";
        if (!_deduper.TryAdd(externalId))
        {
            return null;
        }

        var link = string.IsNullOrWhiteSpace(message.ChatUsername)
            ? null
            : $"https://t.me/{message.ChatUsername}/{message.MessageId.ToString(CultureInfo.InvariantCulture)}";

        return new RawMessage(
            _config.SourceId,
            externalId,
            message.Text,
            message.Author,
            message.ChatTitle,
            link,
            message.PostedAt.ToUniversalTime());
    }

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
    }

    /// <summary>Разобранный секрет сессии MTProto.</summary>
    private readonly record struct MtProtoSession(
        int ApiId,
        string ApiHash,
        string? SessionData,
        string? Phone);
}

/// <summary>
/// Фабрика адаптера MTProto. Обслуживает <c>telegram_chat</c> в режиме
/// <c>userbot</c> (в том числе когда <c>mode</c> не задан); режим <c>bot</c> — Bot API.
/// </summary>
public sealed class TelegramMtProtoAdapterFactory(
    TimeProvider timeProvider,
    ITelegramClientTransportFactory transportFactory) : ISourceAdapterFactory
{
    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ITelegramClientTransportFactory _transportFactory = transportFactory
        ?? throw new ArgumentNullException(nameof(transportFactory));

    /// <inheritdoc />
    public string SourceType => TelegramSourceSettings.SourceType;

    /// <inheritdoc />
    public bool CanHandle(SourceRuntimeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return string.Equals(config.SourceType, SourceType, StringComparison.Ordinal) &&
               !TelegramSourceSettings.IsBotMode(config);
    }

    /// <inheritdoc />
    public IDataSource Create(SourceRuntimeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!CanHandle(config))
        {
            throw new NotSupportedException(
                "MTProto обслуживает только источник telegram_chat в режиме userbot.");
        }

        return new TelegramMtProtoAdapter(config, _transportFactory, _timeProvider);
    }
}
