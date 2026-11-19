namespace Observations.Domain;

/// <summary>
/// Черновик нового наблюдения: данные, которые приходят из прикладного слоя.
/// Вычисляемые и неизменяемые поля (<c>Id</c>, <c>ExpiresAt</c>) сюда не входят —
/// их задаёт <see cref="Observation.Create"/>.
/// </summary>
/// <param name="MapObjectId">Фиксация, к которой относится наблюдение.</param>
/// <param name="SourceId">Источник, по которому сделано наблюдение (для ручной отметки — выбранный пользователем).</param>
/// <param name="SourceStatusId">Статус из списка этого источника.</param>
/// <param name="Kind">Тип источника наблюдения.</param>
/// <param name="ObservedAt">Время наблюдения по данным источника.</param>
/// <param name="ReceivedAt">Время получения наблюдения системой.</param>
/// <param name="ReporterId">Пользователь для <see cref="SourceKind.Manual"/> и <see cref="SourceKind.Moderator"/>.</param>
/// <param name="Confidence">Достоверность 0..1; информационный показатель, на выбор статуса не влияет.</param>
/// <param name="RawPayloadRef">Ссылка на исходное сообщение или иные исходные данные.</param>
/// <param name="Comment">Необязательный текст ручной отметки.</param>
/// <param name="Address">Адрес из сообщения чата («улица дом»); у ручных отметок не заполняется.</param>
public sealed record ObservationDraft(
    Guid MapObjectId,
    Guid SourceId,
    Guid SourceStatusId,
    SourceKind Kind,
    DateTimeOffset ObservedAt,
    DateTimeOffset ReceivedAt,
    Guid? ReporterId = null,
    float? Confidence = null,
    string? RawPayloadRef = null,
    string? Comment = null,
    string? Address = null);
