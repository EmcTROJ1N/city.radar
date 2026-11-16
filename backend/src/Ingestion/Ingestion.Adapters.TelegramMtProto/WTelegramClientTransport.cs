using System.Globalization;
using System.Text;

using Ingestion.Abstractions;

using TL;

namespace Ingestion.Adapters.TelegramMtProto;

/// <summary>
/// Реальный транспорт WTelegramClient. Скрыт за
/// <see cref="ITelegramClientTransport"/>, поэтому в тестах вместо него используется заглушка,
/// а реальные каналы проверяются ручным чек-листом.
/// </summary>
/// <remarks>
/// Сессия хранится в секрете (<see cref="MtProtoTransportConfig.SessionData"/>), файл сессии
/// не используется. Прокси: SOCKS5/HTTP — через <see cref="ProxyTcpConnector"/> (TcpHandler),
/// MTProxy — через <c>MTProxyUrl</c> WTelegramClient. Новые сообщения читаются опросом истории
/// чата от последнего известного id.
/// </remarks>
public sealed class WTelegramClientTransport : ITelegramClientTransport
{
    private static readonly string[] AuthErrorMarkers =
    [
        "AUTH_KEY_UNREGISTERED",
        "AUTH_KEY_INVALID",
        "AUTH_KEY_DUPLICATED",
        "SESSION_REVOKED",
        "SESSION_EXPIRED",
        "USER_DEACTIVATED",
        "PHONE_NUMBER_BANNED",
        "USER_DEACTIVATED_BAN",
    ];

    private static readonly string[] ConfigurationErrorMarkers =
    [
        "CHANNEL_PRIVATE",
        "CHANNEL_INVALID",
        "PEER_ID_INVALID",
        "USERNAME_NOT_OCCUPIED",
        "USERNAME_INVALID",
        "CHAT_NOT_FOUND",
    ];

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly IMtProtoSessionStore _sessionStore;
    private readonly TimeProvider _timeProvider;

    private WTelegram.Client? _client;
    private TL.InputPeer? _peer;
    private int _lastMessageId;
    private string _chatTitle = string.Empty;
    private string? _chatUsername;

    /// <summary>Создаёт транспорт WTelegramClient.</summary>
    /// <param name="sessionStore">Куда сохранять обновлённую сессию (по умолчанию no-op).</param>
    /// <param name="timeProvider">Часы паузы между опросами истории (в тестах не используется).</param>
    public WTelegramClientTransport(
        IMtProtoSessionStore? sessionStore = null,
        TimeProvider? timeProvider = null)
    {
        _sessionStore = sessionStore ?? NullMtProtoSessionStore.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task OpenAsync(
        MtProtoTransportConfig config,
        string chatId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);

        var client = new WTelegram.Client(
            what => ReadConfig(what, config),
            DecodeSession(config.SessionData) ?? [],
            updated => SaveSession(config.SourceId, updated));

        client.FloodRetryThreshold = 0;

        _client = client;

        try
        {
            // Прокси обязателен: сначала проверяем достижимость самого прокси (соединение только
            // с ним), и лишь затем создаём клиент. Прямого fallback нет.
            if (config.Proxy is { } proxy &&
                proxy.Scheme.Trim().ToLowerInvariant() is "socks5" or "socks5h" or "socks" or "http" or "https")
            {
                await ProxyHttpHandlerFactory
                    .EnsureAvailableAsync(proxy, TimeSpan.FromSeconds(15), ct)
                    .ConfigureAwait(false);
            }

            ApplyProxy(client, config.Proxy);

            await client.LoginUserIfNeeded().ConfigureAwait(false);
            await OpenChatAsync(client, chatId, ct).ConfigureAwait(false);
        }
        catch (TelegramTransportException)
        {
            throw;
        }
        catch (ProxyConfigurationException exception)
        {
            throw new TelegramTransportException(
                TelegramFailureKind.Configuration,
                exception.Message,
                innerException: exception);
        }
        catch (TL.RpcException exception)
        {
            throw Map(exception);
        }
        catch (Exception exception)
        {
            throw new TelegramTransportException(
                TelegramFailureKind.Network,
                $"Не удалось подключиться к Telegram: {exception.Message}",
                innerException: exception);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MtProtoIncomingMessage> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var client = _client
                ?? throw new TelegramTransportException(
                    TelegramFailureKind.Auth,
                    "Клиент Telegram не создан.");

            if (client.Disconnected)
            {
                // Обрыв соединения — сетевая ошибка, а не разлогин: WTelegramClient сам
                // переподключается, а если не смог — адаптер повторит с backoff. Бан и разлогин
                // приходят отдельно как RpcException 401/AUTH_KEY_*.
                throw new TelegramTransportException(
                    TelegramFailureKind.Network,
                    "Соединение Telegram разорвано: требуется переподключение.");
            }

            List<MtProtoIncomingMessage> batch;
            try
            {
                var history = await client.Messages_GetHistory(
                        _peer!,
                        limit: 50,
                        min_id: _lastMessageId)
                    .ConfigureAwait(false);

                batch = ReadBatch(history);
            }
            catch (TL.RpcException exception)
            {
                throw Map(exception);
            }
            catch (TelegramTransportException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new TelegramTransportException(
                    TelegramFailureKind.Network,
                    $"Ошибка чтения истории Telegram: {exception.Message}",
                    innerException: exception);
            }

            foreach (var message in batch)
            {
                yield return message;
            }

            if (batch.Count == 0)
            {
                await Task.Delay(PollInterval, _timeProvider, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Разрешает чат по @username/ссылке/числовому id среди диалогов аккаунта.</summary>
    private async Task OpenChatAsync(
        WTelegram.Client client,
        string chatId,
        CancellationToken ct)
    {
        var username = chatId.StartsWith('@') ? chatId[1..] : null;
        var numeric = long.TryParse(
            chatId,
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : (long?)null;

        // Публичный чат/канал адресуется по username: resolve работает и для каналов,
        // в которых аккаунт не состоит, — в отличие от перебора диалогов.
        if (username is not null)
        {
            await ResolveUsernameAsync(client, username, ct).ConfigureAwait(false);
        }

        if (_peer is null)
        {
            var dialogs = await client.Messages_GetAllDialogs().ConfigureAwait(false);

            foreach (var chat in dialogs.chats.Values)
            {
                switch (chat)
                {
                    case TL.Channel channel when MatchesChannel(channel, username, numeric):
                        _peer = new TL.InputPeerChannel(channel.id, channel.access_hash);
                        _chatTitle = channel.title;
                        _chatUsername = channel.username;

                        break;

                    case TL.Chat basicChat when numeric is { } requested &&
                                                MatchesNumeric(basicChat.id, requested):
                        _peer = new TL.InputPeerChat(basicChat.id);
                        _chatTitle = basicChat.title;

                        break;

                    default:
                        continue;
                }

                break;
            }

            if (_peer is null)
            {
                foreach (var user in dialogs.users.Values)
                {
                    if (user is not TL.User account || numeric is not { } requested ||
                        !MatchesNumeric(account.id, requested))
                    {
                        continue;
                    }

                    _peer = new TL.InputPeerUser(account.id, account.access_hash);
                    _chatTitle = $"{account.first_name} {account.last_name}".Trim();
                    _chatUsername = account.username;

                    break;
                }
            }
        }

        ct.ThrowIfCancellationRequested();

        if (_peer is null)
        {
            throw new TelegramTransportException(
                TelegramFailureKind.Configuration,
                $"Чат \"{chatId}\" не найден среди диалогов аккаунта: проверьте chatId и участие аккаунта в чате.");
        }

        // Текущее состояние чата не отдаём: адаптер читает только новые сообщения.
        var latest = await client.Messages_GetHistory(_peer, limit: 1).ConfigureAwait(false);
        _lastMessageId = MaxMessageId(latest.Messages);
    }

    private List<MtProtoIncomingMessage> ReadBatch(TL.Messages_MessagesBase history)
    {
        var batch = new List<MtProtoIncomingMessage>();
        var maxId = _lastMessageId;

        foreach (var item in history.Messages)
        {
            if (item is not TL.Message message)
            {
                continue;
            }

            maxId = Math.Max(maxId, message.id);

            if (message.id <= _lastMessageId ||
                message.flags.HasFlag(TL.Message.Flags.out_) ||
                string.IsNullOrWhiteSpace(message.message))
            {
                continue;
            }

            batch.Add(new MtProtoIncomingMessage(
                message.id,
                message.message,
                ReadAuthor(message),
                _chatTitle,
                _chatUsername,
                new DateTimeOffset(DateTime.SpecifyKind(message.date, DateTimeKind.Utc))));
        }

        _lastMessageId = maxId;

        return batch
            .OrderBy(message => message.MessageId)
            .ToList();
    }

    private static int MaxMessageId(IEnumerable<TL.MessageBase> messages)
    {
        var max = 0;

        foreach (var item in messages)
        {
            if (item is TL.Message message)
            {
                max = Math.Max(max, message.id);
            }
        }

        return max;
    }

    /// <summary>
    /// Разрешает публичный чат/канал/пользователя по username. Ошибка resolve не фатальна:
    /// вызывающий код попробует найти чат среди диалогов аккаунта.
    /// </summary>
    private async Task ResolveUsernameAsync(
        WTelegram.Client client,
        string username,
        CancellationToken ct)
    {
        TL.Contacts_ResolvedPeer resolved;
        try
        {
            resolved = await client.Contacts_ResolveUsername(username).ConfigureAwait(false);
        }
        catch (TL.RpcException)
        {
            return;
        }

        ct.ThrowIfCancellationRequested();

        switch (resolved.peer)
        {
            case TL.PeerChannel peerChannel when
                resolved.chats.TryGetValue(peerChannel.channel_id, out var found) &&
                found is TL.Channel channel:
                _peer = new TL.InputPeerChannel(channel.id, channel.access_hash);
                _chatTitle = channel.title;
                _chatUsername = channel.username;
                break;

            case TL.PeerChat peerChat when
                resolved.chats.TryGetValue(peerChat.chat_id, out var found) &&
                found is TL.Chat chat:
                _peer = new TL.InputPeerChat(chat.id);
                _chatTitle = chat.title;
                break;

            case TL.PeerUser peerUser when resolved.users.TryGetValue(peerUser.user_id, out var user):
                _peer = new TL.InputPeerUser(user.id, user.access_hash);
                _chatTitle = $"{user.first_name} {user.last_name}".Trim();
                _chatUsername = user.username;
                break;
        }
    }

    private static bool MatchesChannel(TL.Channel channel, string? username, long? numeric)
    {
        if (username is not null)
        {
            return string.Equals(channel.username, username, StringComparison.OrdinalIgnoreCase);
        }

        return numeric is { } requested && MatchesNumeric(channel.id, requested);
    }

    /// <summary>Сравнивает id сущности Telegram с нормализованным id источника (<c>-100…</c>).</summary>
    private static bool MatchesNumeric(long entityId, long requested)
    {
        if (entityId == requested)
        {
            return true;
        }

        if (requested >= 0)
        {
            return entityId == requested;
        }

        var absolute = -requested;

        // Супергруппы в Bot API/ссылках имеют префикс -100, у MTProto id — без него.
        if (absolute >= 1_000_000_000_000L)
        {
            absolute -= 1_000_000_000_000L;
        }

        return entityId == absolute;
    }

    private static string? ReadAuthor(TL.Message message)
    {
        if (!string.IsNullOrWhiteSpace(message.post_author))
        {
            return message.post_author;
        }

        return message.from_id is TL.PeerUser user
            ? user.user_id.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static string ReadConfig(string what, MtProtoTransportConfig config) => what switch
    {
        "api_id" => config.ApiId.ToString(CultureInfo.InvariantCulture),
        "api_hash" => config.ApiHash,
        "phone_number" => config.Phone ?? string.Empty,
        "session_pathname" => string.Empty,
        "verification_code" => string.Empty,
        "password" => string.Empty,
        "first_name" => string.Empty,
        "last_name" => string.Empty,
        _ => string.Empty,
    };

    private static void ApplyProxy(WTelegram.Client client, ProxySettings? proxy)
    {
        if (proxy is null)
        {
            return;
        }

        var scheme = proxy.Scheme.Trim().ToLowerInvariant();

        if (scheme is "mtproxy" or "mtproxysecret")
        {
            if (string.IsNullOrWhiteSpace(proxy.Password))
            {
                throw new ProxyConfigurationException(
                    "Для MTProxy нужен секрет прокси: укажите его в поле пароля секрета прокси.");
            }

            client.MTProxyUrl =
                "https://t.me/proxy" +
                $"?server={Uri.EscapeDataString(proxy.Host)}" +
                $"&port={proxy.Port.ToString(CultureInfo.InvariantCulture)}" +
                $"&secret={Uri.EscapeDataString(proxy.Password)}";

            return;
        }

        if (scheme is not ("socks5" or "socks5h" or "socks" or "http" or "https"))
        {
            throw new ProxyConfigurationException(
                $"Схема прокси \"{proxy.Scheme}\" не поддерживается MTProto-транспортом.");
        }

        client.TcpHandler = ProxyTcpConnector.Create(proxy);
    }

    private static byte[]? DecodeSession(string? sessionData)
    {
        if (string.IsNullOrWhiteSpace(sessionData))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(sessionData);
        }
        catch (FormatException)
        {
            // Админ мог вставить сессию как текст: WTelegramClient примет сырые байты.
            return Encoding.UTF8.GetBytes(sessionData);
        }
    }

    private void SaveSession(Guid sourceId, byte[] session)
    {
        var value = Convert.ToBase64String(session);

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await _sessionStore
                        .SaveAsync(sourceId, value, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Сохранение сессии не должно ронять транспорт: сессия останется в памяти.
                }
            },
            CancellationToken.None);
    }

    private static TelegramTransportException Map(TL.RpcException exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? "Telegram RPC error"
            : exception.Message;

        if (exception.Code == 420 ||
            message.Contains("FLOOD_WAIT", StringComparison.OrdinalIgnoreCase))
        {
            return new TelegramTransportException(
                TelegramFailureKind.FloodWait,
                $"Telegram просит подождать: {message}",
                TimeSpan.FromSeconds(Math.Max(exception.X, 1)),
                exception);
        }

        if (exception.Code == 401 ||
            AuthErrorMarkers.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return new TelegramTransportException(
                TelegramFailureKind.Auth,
                $"Сессия Telegram недействительна (разлогин или бан): {message}",
                innerException: exception);
        }

        if (ConfigurationErrorMarkers.Any(
                marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return new TelegramTransportException(
                TelegramFailureKind.Configuration,
                $"Чат недоступен аккаунту: {message}",
                innerException: exception);
        }

        return new TelegramTransportException(
            TelegramFailureKind.Network,
            $"Ошибка Telegram: {message}",
            innerException: exception);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _client = null;

        return ValueTask.CompletedTask;
    }
}

/// <summary>Фабрика реального транспорта WTelegramClient; подменяется в тестах.</summary>
public sealed class WTelegramClientTransportFactory(
    IMtProtoSessionStore? sessionStore = null,
    TimeProvider? timeProvider = null) : ITelegramClientTransportFactory
{
    private readonly IMtProtoSessionStore _sessionStore = sessionStore ?? NullMtProtoSessionStore.Instance;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public ITelegramClientTransport Create() => new WTelegramClientTransport(_sessionStore, _timeProvider);
}
