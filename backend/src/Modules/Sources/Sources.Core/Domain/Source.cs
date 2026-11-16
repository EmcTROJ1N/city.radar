namespace Sources.Core.Domain;

/// <summary>
/// Источник данных (таблица <c>sources</c>): чат Telegram или MAX с промптом-фильтром,
/// режимом определения фиксации по тексту и состоянием health-check. Идентификатор чата хранится
/// в <see cref="Settings"/> (jsonb), как в эталоне схемы; сессия и прокси — ссылки на
/// <c>secrets(id)</c>.
/// </summary>
internal sealed class Source
{
    public Guid Id { get; set; }

    /// <summary>Название источника.</summary>
    public string Name { get; set; } = null!;

    /// <summary>Код типа адаптера из <c>source_types</c>: <c>telegram_chat</c> или <c>max_chat</c>.</summary>
    public string SourceType { get; set; } = null!;

    /// <summary>Параметры типа, в первую очередь идентификатор чата: <c>{"chatId":"@name"}</c>.</summary>
    public string Settings { get; set; } = "{}";

    /// <summary>Промпт-фильтр пользователя; системный промпт добавляется автоматически.</summary>
    public string UserPrompt { get; set; } = string.Empty;

    /// <summary>Режим «определять фиксацию по тексту сообщения»; по умолчанию включён.</summary>
    public bool LinkByText { get; set; } = true;

    public bool IsEnabled { get; set; } = true;

    /// <summary>Состояние health-check: <c>unknown</c>, <c>ok</c>, <c>silent</c>, <c>flood_wait</c>,
    /// <c>auth_error</c>, <c>error</c> (check в БД).</summary>
    public string State { get; set; } = "unknown";

    public DateTimeOffset? LastMessageAt { get; set; }

    /// <summary>
    /// Деталь health-check для UI: срок окончания FloodWait (ISO-8601) или текст ошибки
    /// авторизации/адаптера. В эталоне <c>db/schema.sql</c> колонки нет — она добавляется
    /// миграцией <c>SourceHealth</c>.
    /// </summary>
    public string? StateDetail { get; set; }

    /// <summary>
    /// Telegram-сессия, через которую читается источник (<c>secrets.kind = telegram_session</c>).
    /// Допустима только для <c>telegram_chat</c>; <c>null</c> — сессия не назначена.
    /// </summary>
    public Guid? TelegramSessionId { get; set; }

    /// <summary>
    /// Прокси источника (<c>secrets.kind = proxy</c>); первый в порядке разрешения
    /// «источник → аккаунт → глобальный».
    /// </summary>
    public Guid? ProxyId { get; set; }

    /// <summary>Кто создал источник; <c>null</c> для записей, созданных вне API.</summary>
    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Обновляется триггером <c>sources_updated</c> при любом UPDATE.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Политика источника; создаётся и удаляется вместе с источником.</summary>
    public SourcePolicy? Policy { get; set; }
}
