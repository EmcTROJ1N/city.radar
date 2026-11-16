using System.Globalization;
using System.Text.Json;

namespace Ingestion.Abstractions;

/// <summary>
/// Настройки источника типа <c>telegram_chat</c>. Один тип
/// источника поддерживает два способа чтения: юзербот MTProto (<c>mode = "userbot"</c>,
/// по умолчанию) и Telegram Bot API (<c>mode = "bot"</c>). Режим выбирает фабрику адаптера.
/// </summary>
public static class TelegramSourceSettings
{
    /// <summary>Код типа источника Telegram в <c>source_types.code</c>.</summary>
    public const string SourceType = "telegram_chat";

    /// <summary>Режим чтения через Telegram Bot API (собственные чаты проекта).</summary>
    public const string BotMode = "bot";

    /// <summary>Режим чтения через юзербот MTProto (сторонние группы).</summary>
    public const string UserbotMode = "userbot";

    /// <summary>
    /// Режим чтения источника: <c>bot</c> или <c>userbot</c>. Отсутствие и неизвестное значение
    /// означают <c>userbot</c> — историческое поведение типа <c>telegram_chat</c>.
    /// </summary>
    /// <param name="config">Снимок источника.</param>
    public static string GetMode(SourceRuntimeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return GetMode(config.Settings);
    }

    /// <summary>Режим чтения по <c>settings</c>; см. <see cref="GetMode(SourceRuntimeConfig)"/>.</summary>
    /// <param name="settings">JSON настроек источника.</param>
    public static string GetMode(JsonElement settings)
    {
        var mode = FindString(settings, "mode")?.Trim().ToLowerInvariant();

        return string.IsNullOrEmpty(mode) ? UserbotMode : mode;
    }

    /// <summary><c>true</c>, если источник читается через Bot API.</summary>
    /// <param name="config">Снимок источника.</param>
    public static bool IsBotMode(SourceRuntimeConfig config) =>
        string.Equals(GetMode(config), BotMode, StringComparison.Ordinal);

    /// <summary>Сырое значение <c>settings.chatId</c>; <c>null</c>, если не задано.</summary>
    /// <param name="config">Снимок источника.</param>
    public static string? GetRawChatId(SourceRuntimeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return FindString(config.Settings, "chatId") ?? FindString(config.Settings, "chat_id");
    }

    /// <summary>
    /// Нормализованный идентификатор чата: <c>@username</c> в нижнем регистре или числовой id
    /// (включая отрицательные id супергрупп вида <c>-100…</c>). Принимаются <c>@name</c>,
    /// число и ссылки <c>t.me/name</c>, <c>telegram.me/name</c>, <c>tg://resolve?domain=name</c>.
    /// </summary>
    /// <param name="config">Снимок источника.</param>
    /// <param name="chatId">Нормализованный идентификатор.</param>
    /// <param name="error">Причина, если настройка отсутствует или некорректна.</param>
    public static bool TryGetChatId(
        SourceRuntimeConfig config,
        out string chatId,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(config);

        var raw = GetRawChatId(config);
        if (string.IsNullOrWhiteSpace(raw))
        {
            chatId = string.Empty;
            error = "Не задан chatId источника: укажите @username, числовой id или ссылку t.me.";

            return false;
        }

        try
        {
            chatId = NormalizeChatId(raw);
            error = null;

            return true;
        }
        catch (FormatException exception)
        {
            chatId = string.Empty;
            error = exception.Message;

            return false;
        }
    }

    /// <summary>
    /// Приводит значение <c>settings.chatId</c> к каноническому виду: <c>@username</c>
    /// (нижний регистр) или числовой id.
    /// </summary>
    /// <param name="raw">Сырое значение: @name, число или ссылка t.me.</param>
    /// <exception cref="FormatException">Значение не является адресуемым чатом (например, инвайт-ссылка).</exception>
    public static string NormalizeChatId(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);

        var value = raw.Trim();

        if (value.StartsWith("tg://", StringComparison.OrdinalIgnoreCase))
        {
            // tg://resolve?domain=name — публичная ссылка на чат/канал.
            var queryIndex = value.IndexOf('?', StringComparison.Ordinal);
            if (queryIndex > 0)
            {
                foreach (var pair in value[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var separator = pair.IndexOf('=', StringComparison.Ordinal);
                    if (separator <= 0 ||
                        !string.Equals(pair[..separator], "domain", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    value = pair[(separator + 1)..];
                    break;
                }
            }
        }
        else if (value.Contains("t.me/", StringComparison.OrdinalIgnoreCase) ||
                 value.Contains("telegram.me/", StringComparison.OrdinalIgnoreCase))
        {
            var start = value.IndexOf("t.me/", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                start = value.IndexOf("telegram.me/", StringComparison.OrdinalIgnoreCase);
                start = start < 0 ? start : start + "telegram.me/".Length;
            }
            else
            {
                start += "t.me/".Length;
            }

            var rest = value[start..];
            var end = rest.IndexOfAny(['/', '?', '#']);
            value = end >= 0 ? rest[..end] : rest;
        }

        if (value.StartsWith('@'))
        {
            value = value[1..];
        }

        if (value.StartsWith("joinchat/", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith('+'))
        {
            throw new FormatException(
                "Приватная ссылка-приглашение Telegram не содержит идентификатор чата. " +
                "Укажите @username или числовой id чата.");
        }

        if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id))
        {
            return id.ToString(CultureInfo.InvariantCulture);
        }

        if (value.Length > 0 && value.All(character => char.IsLetterOrDigit(character) || character == '_'))
        {
            return $"@{value.ToLowerInvariant()}";
        }

        throw new FormatException(
            $"Некорректный chatId \"{raw}\": укажите @username, числовой id или ссылку t.me.");
    }

    private static string? FindString(JsonElement settings, string name)
    {
        if (settings.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in settings.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                _ => null,
            };
        }

        return null;
    }
}

/// <summary>
/// Сопоставление чата сообщения Telegram с <c>chatId</c> источника: фильтр пропускает
/// только сообщения разрешённого чата. <c>@username</c> сравнивается без учёта регистра,
/// числовой id — точно.
/// </summary>
public sealed class TelegramChatMatcher
{
    private readonly string _normalizedChatId;
    private readonly long? _numericChatId;
    private readonly string? _username;

    /// <summary>Создаёт сопоставление по нормализованному значению <c>chatId</c>.</summary>
    /// <param name="normalizedChatId">Результат <see cref="TelegramSourceSettings.NormalizeChatId"/>.</param>
    public TelegramChatMatcher(string normalizedChatId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedChatId);

        _normalizedChatId = normalizedChatId;

        if (normalizedChatId.StartsWith('@'))
        {
            _username = normalizedChatId[1..];
        }
        else if (long.TryParse(
                     normalizedChatId,
                     NumberStyles.AllowLeadingSign,
                     CultureInfo.InvariantCulture,
                     out var id))
        {
            _numericChatId = id;
        }
    }

    /// <summary>Нормализованный <c>chatId</c>, который используется в <c>external_id</c>.</summary>
    public string NormalizedChatId => _normalizedChatId;

    /// <summary>
    /// <c>true</c>, если сообщение принадлежит чату источника.
    /// </summary>
    /// <param name="username">Username чата из Telegram; <c>null</c>, если его нет.</param>
    /// <param name="chatId">Числовой id чата из Telegram.</param>
    public bool Matches(string? username, long chatId)
    {
        if (_numericChatId is { } numeric)
        {
            // Telegram отдаёт id супергрупп/каналов с префиксом -100, а админ мог указать
            // «сырой» id без префикса; оба варианта адресуют один и тот же чат.
            if (numeric == chatId)
            {
                return true;
            }

            if (numeric > 0)
            {
                return chatId == -numeric ||
                       chatId == -(1_000_000_000_000L + numeric);
            }

            if (numeric <= -1_000_000_000_000L)
            {
                return chatId == numeric + 1_000_000_000_000L;
            }

            return numeric != 0 && chatId == -numeric;
        }

        if (_username is null || string.IsNullOrEmpty(username))
        {
            return false;
        }

        var candidate = username.StartsWith('@') ? username[1..] : username;

        return string.Equals(candidate, _username, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Окно дедупликации <c>external_id</c> в памяти адаптера: Telegram Bot API при повторном
/// long polling может отдать то же обновление, а MTProto — то же сообщение в истории. Окно
/// ограничено, чтобы память не росла бесконечно.
/// </summary>
public sealed class ExternalIdDeduper
{
    private readonly int _capacity;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _sync = new();

    /// <summary>Создаёт окно дедупликации.</summary>
    /// <param name="capacity">Сколько последних идентификаторов помнить (по умолчанию 2000).</param>
    public ExternalIdDeduper(int capacity = 2000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _capacity = capacity;
    }

    /// <summary>
    /// Регистрирует идентификатор. <c>true</c> — он новый, сообщение нужно обработать; <c>false</c> —
    /// дубль, сообщение пропускается.
    /// </summary>
    /// <param name="externalId">Внешний идентификатор сообщения.</param>
    public bool TryAdd(string externalId)
    {
        ArgumentNullException.ThrowIfNull(externalId);

        lock (_sync)
        {
            if (!_seen.Add(externalId))
            {
                return false;
            }

            _order.Enqueue(externalId);
            while (_order.Count > _capacity)
            {
                _seen.Remove(_order.Dequeue());
            }

            return true;
        }
    }
}

/// <summary>
/// Экспоненциальный backoff ошибок адаптера: 1 с, 2 с, 4 с… с верхней границей
/// 5 минут. FloodWait использует собственное время ожидания из ответа Telegram, а не этот backoff.
/// </summary>
public sealed class SourceRetryBackoff
{
    /// <summary>Верхняя граница задержки — 5 минут.</summary>
    public static readonly TimeSpan DefaultMax = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private int _attempt;

    /// <summary>Создаёт backoff.</summary>
    /// <param name="baseDelay">Базовая задержка (по умолчанию 1 секунда).</param>
    /// <param name="maxDelay">Верхняя граница (по умолчанию 5 минут).</param>
    public SourceRetryBackoff(TimeSpan? baseDelay = null, TimeSpan? maxDelay = null)
    {
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        _maxDelay = maxDelay ?? DefaultMax;

        if (_baseDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(baseDelay), "Базовая задержка должна быть положительной.");
        }

        if (_maxDelay < _baseDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDelay), "Верхняя граница не меньше базовой задержки.");
        }
    }

    /// <summary>Следующая задержка; попытки удваиваются, но не превышают верхнюю границу.</summary>
    public TimeSpan Next()
    {
        var exponent = Math.Min(_attempt, 10);
        _attempt++;

        var ticks = _baseDelay.Ticks * Math.Pow(2, exponent);
        var delay = TimeSpan.FromTicks((long)Math.Min(ticks, _maxDelay.Ticks));

        return delay < _baseDelay ? _baseDelay : delay;
    }

    /// <summary>Сбрасывает счётчик после успешного подключения.</summary>
    public void Reset() => _attempt = 0;
}
