namespace Moderation.Core.Domain;

/// <summary>
/// Строка таблицы <c>moderation_cases</c>. Сущность внутренняя
/// для модуля: снаружи доступны только <c>Moderation.Contracts</c> и эндпоинты.
/// </summary>
/// <remarks>
/// Колонка <c>outbox_message_id</c> добавлена миграцией <c>Moderation</c> поверх эталона
/// <c>db/schema.sql</c> как ключ идемпотентности обработчика <c>ModerationCaseRequested</c>:
/// повторная доставка сообщения не создаёт второй кейс.
/// </remarks>
internal sealed class ModerationCase
{
    public Guid Id { get; set; }

    /// <summary>Вид кейса; значения — <see cref="ModerationCaseKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Состояние; значения — <see cref="ModerationCaseStates"/>.</summary>
    public string State { get; set; } = ModerationCaseStates.Open;

    public Guid? SourceId { get; set; }

    public Guid? RawMessageId { get; set; }

    public Guid? ObservationId { get; set; }

    public Guid? MapObjectId { get; set; }

    /// <summary>Автор жалобы (только для <c>complaint</c>, check в БД).</summary>
    public Guid? ReporterId { get; set; }

    /// <summary>Текст жалобы (только для <c>complaint</c>).</summary>
    public string? Comment { get; set; }

    /// <summary>Предложение системы в <c>jsonb</c>: статус, confidence, evidence_quote, причина.</summary>
    public string? Proposed { get; set; }

    /// <summary>Решение модератора в <c>jsonb</c> (заполняется).</summary>
    public string? Resolution { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? ResolvedBy { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>Идентификатор outbox-сообщения — ключ идемпотентности; уникален в БД.</summary>
    public Guid? OutboxMessageId { get; set; }
}
