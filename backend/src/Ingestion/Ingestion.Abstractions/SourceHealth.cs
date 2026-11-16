namespace Ingestion.Abstractions;

/// <summary>
/// Результат health-check источника: состояние, человекочитаемая деталь ошибки
/// и время последнего полученного сообщения.
/// </summary>
/// <param name="State">Текущее состояние источника.</param>
/// <param name="Detail">Деталь для UI: срок FloodWait, текст ошибки авторизации и т. п.; <c>null</c>, если её нет.</param>
/// <param name="LastMessageAt">Время последнего сообщения источника; <c>null</c>, если сообщений ещё не было.</param>
public sealed record SourceHealth(
    SourceState State,
    string? Detail,
    DateTimeOffset? LastMessageAt);
