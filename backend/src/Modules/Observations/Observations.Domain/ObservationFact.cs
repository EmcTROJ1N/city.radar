namespace Observations.Domain;

/// <summary>
/// Факт наблюдения, с которым работает <see cref="StatusResolver"/>.
/// Это снимок данных наблюдения без привязки к хранилищу: <see cref="ExpiresAt"/> уже
/// зафиксирован при создании наблюдения и не пересчитывается при смене таймаута источника.
/// </summary>
/// <param name="Id">Идентификатор наблюдения.</param>
/// <param name="SourceId">Источник наблюдения.</param>
/// <param name="SourceStatusId">Статус из списка источника.</param>
/// <param name="Kind">Тип источника наблюдения.</param>
/// <param name="ObservedAt">Время наблюдения по данным источника.</param>
/// <param name="ReceivedAt">Время получения наблюдения системой; разрешает ничью по <paramref name="ObservedAt"/>.</param>
/// <param name="ExpiresAt">Момент, после которого наблюдение не учитывается (<c>ObservedAt + timeout</c>).</param>
/// <param name="SourceTimeout">Таймаут источника на момент создания наблюдения; задаёт границы Fresh/Aging.</param>
/// <param name="IsRetracted">Наблюдение отозвано модератором и не учитывается.</param>
/// <param name="Tone">Смысл статуса для сводки по зоне.</param>
/// <param name="PartialStatusId">
/// Статус источника для смешанной сводки по зоне — первый «ограниченный» по порядку показа;
/// <c>null</c>, если у источника такого статуса нет.
/// </param>
public sealed record ObservationFact(
    Guid Id,
    Guid SourceId,
    Guid SourceStatusId,
    SourceKind Kind,
    DateTimeOffset ObservedAt,
    DateTimeOffset ReceivedAt,
    DateTimeOffset ExpiresAt,
    TimeSpan SourceTimeout,
    bool IsRetracted,
    StatusTone Tone = StatusTone.Positive,
    Guid? PartialStatusId = null);
