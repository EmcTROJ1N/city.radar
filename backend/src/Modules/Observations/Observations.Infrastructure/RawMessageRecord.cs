namespace Observations.Infrastructure;

/// <summary>
/// Строка таблицы <c>raw_messages</c>: исходное сообщение источника, на которое
/// ссылается наблюдение из Telegram/MAX. Через публичный API текст отдаётся, автор и чат —
/// нет. Запись только на чтение для модуля наблюдений на этапе;
/// заполняет её ingestion/moderator-контур.
/// </summary>
internal sealed class RawMessageRecord
{
    public Guid Id { get; set; }

    public Guid SourceId { get; set; }

    /// <summary>Идентификатор сообщения во внешней системе; уникален внутри источника.</summary>
    public string ExternalId { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public string? Author { get; set; }

    public string? Chat { get; set; }

    public string? Link { get; set; }

    public DateTimeOffset PostedAt { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }
}
