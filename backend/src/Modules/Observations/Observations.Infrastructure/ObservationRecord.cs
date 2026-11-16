using Observations.Domain;

namespace Observations.Infrastructure;

/// <summary>
/// Строка таблицы <c>observations</c>. Сущность EF Core внутренняя для модуля:
/// снаружи наблюдение видно только как доменный <see cref="Observation"/>. Запись изменяема
/// на уровне EF только для отзыва — неизменяемость остальных полей стережёт триггер
/// <c>observations_immutable</c>.
/// </summary>
internal sealed class ObservationRecord
{
    public Guid Id { get; set; }

    public Guid MapObjectId { get; set; }

    public Guid SourceId { get; set; }

    public Guid SourceStatusId { get; set; }

    /// <summary>Тип источника в БД: <c>manual</c>, <c>moderator</c>, <c>telegram</c>, <c>max</c>.</summary>
    public string SourceType { get; set; } = string.Empty;

    public Guid? ReporterId { get; set; }

    public DateTimeOffset ObservedAt { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public float? Confidence { get; set; }

    public Guid? RawMessageId { get; set; }

    public string? Comment { get; set; }

    public int? GeoDistanceM { get; set; }

    public bool IsRetracted { get; set; }

    public Guid? RetractedBy { get; set; }

    public DateTimeOffset? RetractedAt { get; set; }

    public string? RetractReason { get; set; }

    /// <summary>Переводит запись в доменное наблюдение через <see cref="Observation.Restore"/>.</summary>
    public Observation ToDomain()
    {
        var draft = new ObservationDraft(
            MapObjectId,
            SourceId,
            SourceStatusId,
            ParseKind(SourceType),
            ObservedAt,
            ReceivedAt,
            ReporterId,
            Confidence,
            RawMessageId?.ToString(),
            Comment);

        return Observation.Restore(Id, draft, ExpiresAt, IsRetracted);
    }

    /// <summary>
    /// Создаёт запись для нового доменного наблюдения. <c>raw_message_id</c> заполняется, только
    /// если источник — Telegram/MAX и <see cref="Observation.RawPayloadRef"/> содержит
    /// идентификатор исходного сообщения (check схемы требует его для этих типов).
    /// </summary>
    public static ObservationRecord FromDomain(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        return new ObservationRecord
        {
            Id = observation.Id,
            MapObjectId = observation.MapObjectId,
            SourceId = observation.SourceId,
            SourceStatusId = observation.SourceStatusId,
            SourceType = ToDb(observation.Kind),
            ReporterId = observation.ReporterId,
            ObservedAt = observation.ObservedAt,
            ReceivedAt = observation.ReceivedAt,
            ExpiresAt = observation.ExpiresAt,
            Confidence = observation.Confidence,
            RawMessageId = ParseRawMessageId(observation),
            Comment = observation.Comment,
            IsRetracted = observation.IsRetracted,
        };
    }

    private static string ToDb(SourceKind kind) => kind switch
    {
        SourceKind.Manual => "manual",
        SourceKind.Moderator => "moderator",
        SourceKind.Telegram => "telegram",
        SourceKind.Max => "max",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный тип источника."),
    };

    internal static SourceKind ParseKind(string value) => value switch
    {
        "manual" => SourceKind.Manual,
        "moderator" => SourceKind.Moderator,
        "telegram" => SourceKind.Telegram,
        "max" => SourceKind.Max,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Неизвестный тип источника."),
    };

    private static Guid? ParseRawMessageId(Observation observation) =>
        observation.Kind is SourceKind.Telegram or SourceKind.Max
        && Guid.TryParse(observation.RawPayloadRef, out var rawMessageId)
            ? rawMessageId
            : null;
}
