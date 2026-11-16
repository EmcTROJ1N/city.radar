namespace Sources.Core.Domain;

/// <summary>
/// Зашифрованный секрет (таблица <c>secrets</c>):
/// Telegram-сессия, прокси или настройки LLM. В <see cref="Ciphertext"/> лежит только шифртекст;
/// открытое значение не хранится и наружу не отдаётся.
/// </summary>
internal sealed class Secret
{
    public Guid Id { get; set; }

    /// <summary>Вид секрета: <c>telegram_session</c>, <c>proxy</c> или <c>llm</c> (check в БД).</summary>
    public string Kind { get; set; } = null!;

    /// <summary>Название для списка секретов; уникально внутри вида (<c>unique (kind, name)</c>).</summary>
    public string Name { get; set; } = null!;

    /// <summary>Шифртекст AES-256-GCM: <c>nonce(12) | tag(16) | data</c>.</summary>
    public byte[] Ciphertext { get; set; } = [];

    /// <summary>Версия мастер-ключа, которой зашифровано значение (<c>secrets.key_version</c>).</summary>
    public int KeyVersion { get; set; }

    /// <summary>
    /// Прокси аккаунта Telegram или LLM. У секрета вида <c>proxy</c> всегда
    /// <c>null</c> — это запрещено check-констрейнтом схемы.
    /// </summary>
    public Guid? ProxyId { get; set; }

    /// <summary>Кто создал секрет; <c>null</c> для записей, созданных вне API.</summary>
    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Обновляется триггером <c>secrets_updated</c> при любом UPDATE.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
