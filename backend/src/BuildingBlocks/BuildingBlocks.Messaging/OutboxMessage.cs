namespace BuildingBlocks.Messaging;

/// <summary>
/// Строка таблицы <c>outbox_messages</c> (<c>db/schema.sql</c>).
/// Сущность внутренняя для блока: снаружи доступен только <see cref="IOutbox"/>, а диспетчер
/// работает с таблицей сырым SQL под <c>FOR UPDATE SKIP LOCKED</c>.
/// </summary>
internal sealed class OutboxMessage
{
    public Guid Id { get; set; }

    /// <summary>Сторона-получатель: <c>api</c> или <c>worker</c> (check в БД).</summary>
    public string Destination { get; set; } = null!;

    /// <summary>Тип события — ключ выбора <see cref="IOutboxHandler"/>.</summary>
    public string Type { get; set; } = null!;

    /// <summary>Тело события в <c>jsonb</c> (JSON-текст, web-настройки System.Text.Json).</summary>
    public string Payload { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Момент, раньше которого сообщение не выбирается (повтор после ошибки).</summary>
    public DateTimeOffset AvailableAt { get; set; }

    /// <summary><c>null</c> — сообщение ещё не обработано.</summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Число неудачных попыток; после <c>MaxAttempts</c> сообщение выводится из очереди.</summary>
    public int Attempts { get; set; }

    /// <summary>Текст последней ошибки обработки (для диагностики).</summary>
    public string? LastError { get; set; }
}
