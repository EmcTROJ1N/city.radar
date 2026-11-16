using System.Text.Json.Serialization;

namespace Observations.Application;

/// <summary>
/// Чтение публичного списка отметок и текущей проекции.
/// SQL-проекции живут в инфраструктуре: прикладной слой не знает об EF Core и PostGIS.
/// </summary>
public interface IObservationReadStore
{
    /// <summary>
    /// Отметки фиксации для публичного списка: только неотозванные, новые сверху. Текст —
    /// <c>raw_messages.text</c> для источников и <c>comment</c> для пользовательских отметок;
    /// идентификатор автора наружу не попадает.
    /// </summary>
    Task<IReadOnlyList<PublicObservationView>> GetPublicAsync(
        Guid mapObjectId,
        int offset,
        int limit,
        CancellationToken ct);

    /// <summary>
    /// Текущий статус фиксации из проекции <c>current_statuses</c>; <c>null</c> — строки нет
    /// или статус «неизвестно» (в контракте это <c>status: null</c>).
    /// </summary>
    Task<CurrentStatusViewDto?> GetCurrentStatusAsync(Guid mapObjectId, CancellationToken ct);

    /// <summary>
    /// Расстояние в метрах от геолокации пользователя до геометрии фиксации.
    /// Считается PostGIS-ом на сервере; координаты пользователя не сохраняются.
    /// </summary>
    Task<int?> GetGeoDistanceMAsync(
        Guid mapObjectId,
        double latitude,
        double longitude,
        CancellationToken ct);
}

/// <summary>Публичная отметка по схеме <c>PublicObservation</c> (без автора).</summary>
/// <param name="Id">Идентификатор наблюдения.</param>
/// <param name="Status">Статус источника, выставленный отметкой.</param>
/// <param name="ObservedAt">Время наблюдения.</param>
/// <param name="ExpiresAt">Момент истечения актуальности.</param>
/// <param name="SourceType">Тип источника: <c>manual</c>, <c>moderator</c>, <c>telegram</c>, <c>max</c>.</param>
/// <param name="SourceName">Название источника; <c>null</c>, если недоступно.</param>
/// <param name="Text">Сообщение чата или комментарий пользователя.</param>
/// <param name="Confidence">Достоверность 0..1; информационный показатель.</param>
/// <param name="GeoDistanceM">Расстояние от пользователя до фиксации, метры.</param>
public sealed record PublicObservationView(
    Guid Id,
    PublicStatusView Status,
    DateTimeOffset ObservedAt,
    DateTimeOffset ExpiresAt,
    string SourceType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceName,
    string? Text,
    float? Confidence,
    int? GeoDistanceM);

/// <summary>Публичные поля статуса источника (схема <c>SourceStatusPublic</c>).</summary>
/// <param name="Id">Идентификатор статуса источника.</param>
/// <param name="Code">Код статуса.</param>
/// <param name="Name">Название статуса.</param>
/// <param name="Color">Ключ палитры оформления.</param>
/// <param name="Icon">Ключ иконки.</param>
public sealed record PublicStatusView(Guid Id, string Code, string Name, string Color, string Icon);

/// <summary>Итоговый статус фиксации по схеме <c>StatusView</c>.</summary>
/// <param name="SourceStatusId">Идентификатор статуса источника.</param>
/// <param name="Code">Код статуса.</param>
/// <param name="Name">Название статуса.</param>
/// <param name="Color">Ключ палитры оформления.</param>
/// <param name="Icon">Ключ иконки.</param>
/// <param name="Freshness">Свежесть: <c>fresh</c>, <c>aging</c> или <c>stale</c>.</param>
/// <param name="ObservedAt">Время победившего наблюдения.</param>
/// <param name="SourceType">Тип источника победившего наблюдения.</param>
/// <param name="ObservationCount">Количество актуальных наблюдений.</param>
public sealed record CurrentStatusViewDto(
    Guid SourceStatusId,
    string Code,
    string Name,
    string Color,
    string Icon,
    string Freshness,
    DateTimeOffset? ObservedAt,
    string? SourceType,
    int ObservationCount);

/// <summary>Страница публичных отметок: элементы и признак следующей страницы.</summary>
/// <param name="Items">Отметки текущей страницы.</param>
/// <param name="HasMore">Есть ли следующая страница.</param>
public sealed record PublicObservationBatch(IReadOnlyList<PublicObservationView> Items, bool HasMore);

/// <summary>Публичный список отметок и текущий статус фиксации.</summary>
public sealed class ObservationQueries(IObservationReadStore store)
{
    /// <summary>
    /// Читает страницу публичных отметок, отозванные не показываются, порядок — <c>observed_at desc</c>.
    /// Одна лишняя строка запрашивается, чтобы понять, есть ли следующая страница.
    /// </summary>
    public async Task<PublicObservationBatch> ListAsync(
        Guid mapObjectId,
        int offset,
        int limit,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var rows = await store.GetPublicAsync(mapObjectId, offset, limit + 1, ct);
        var hasMore = rows.Count > limit;
        var items = hasMore ? rows.Take(limit).ToArray() : [.. rows];

        return new PublicObservationBatch(items, hasMore);
    }

    /// <summary>Текущий статус фиксации для ответа на создание отметки.</summary>
    public Task<CurrentStatusViewDto?> GetCurrentStatusAsync(
        Guid mapObjectId,
        CancellationToken ct) =>
        store.GetCurrentStatusAsync(mapObjectId, ct);
}
