using Observations.Domain;

namespace Observations.Application;

/// <summary>
/// Хранилище наблюдений. Прикладной слой не знает об EF Core:
/// реализация живёт в <c>Observations.Infrastructure</c> и работает с текущим
/// <c>MayakDbContext</c>.
/// </summary>
/// <remarks>
/// Методы изменения не вызывают <c>SaveChanges</c>: добавление наблюдения и отзыв должны
/// сохраняться одной транзакцией вместе с пересчётом проекции и аудитом (–4.5),
/// поэтому сохранение остаётся вызывающему обработчику.
/// </remarks>
public interface IObservationRepository
{
    /// <summary>
    /// Добавляет наблюдение в текущий контекст БД без сохранения. Ограничения схемы
    /// (составной внешний ключ на статус источника, check-констрейнты) проверяются при
    /// <c>SaveChanges</c>.
    /// </summary>
    /// <param name="observation">Созданное доменное наблюдение.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <param name="geoDistanceM">
    /// Расстояние от геолокации пользователя до фиксации в метрах; сохраняется
    /// только как информационный признак и ни на что не влияет.
    /// </param>
    Task AddAsync(Observation observation, CancellationToken ct, int? geoDistanceM = null);

    /// <summary>
    /// Факты наблюдений фиксации для <see cref="StatusResolver"/>: только неотозванные
    /// строки, таймаут источника берётся из <c>source_policies.timeout</c>. Истёкшие наблюдения
    /// не отфильтровываются — это делает резолвер по <see cref="ObservationFact.ExpiresAt"/>.
    /// </summary>
    /// <param name="mapObjectId">Фиксация.</param>
    /// <param name="ct">Токен отмены.</param>
    Task<IReadOnlyList<ObservationFact>> GetFactsAsync(Guid mapObjectId, CancellationToken ct);

    /// <summary>
    /// Является ли фиксация зоной (полигоном): статус зоны считается сводкой
    /// <see cref="StatusResolver.ResolveZone"/>, а не «последняя побеждает».
    /// </summary>
    /// <param name="mapObjectId">Фиксация.</param>
    /// <param name="ct">Токен отмены.</param>
    Task<bool> IsZoneAsync(Guid mapObjectId, CancellationToken ct);

    /// <summary>Наблюдение по идентификатору; <c>null</c>, если его нет.</summary>
    /// <param name="id">Идентификатор наблюдения.</param>
    /// <param name="ct">Токен отмены.</param>
    Task<Observation?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Отзывает наблюдение (единственное допустимое изменение, триггер <c>observations_guard</c>)
    /// в текущем контексте БД без сохранения. Повторный отзыв — нарушение доменного правила.
    /// </summary>
    /// <param name="id">Идентификатор наблюдения.</param>
    /// <param name="by">Модератор, выполнивший отзыв.</param>
    /// <param name="reason">Причина отзыва (обязательна, уходит в аудит).</param>
    /// <param name="at">Серверное время отзыва.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <exception cref="DomainException">Наблюдение не найдено или уже отозвано.</exception>
    Task RetractAsync(Guid id, Guid by, string reason, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// Сколько ещё не отозванных наблюдений подходит под фильтр пакетного отзыва. Проверяется
    /// до изменения, чтобы отклонить слишком широкую операцию.
    /// </summary>
    /// <param name="filter">Критерии отбора (хотя бы один задан).</param>
    /// <param name="ct">Токен отмены.</param>
    Task<long> CountRetractableAsync(BulkRetractFilter filter, CancellationToken ct);

    /// <summary>
    /// Отзывает все подходящие под фильтр и ещё не отозванные наблюдения одним запросом в
    /// текущей транзакции (без <c>SaveChanges</c> вызывающего). Триггер <c>observations_guard</c>
    /// пропускает ровно набор полей отзыва.
    /// </summary>
    /// <param name="filter">Критерии отбора.</param>
    /// <param name="by">Модератор, выполнивший отзыв.</param>
    /// <param name="reason">Причина отзыва (уходит в аудит).</param>
    /// <param name="at">Серверное время отзыва.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Отозванные строки: наблюдение и его фиксация.</returns>
    Task<IReadOnlyList<RetractedObservationRow>> RetractBulkAsync(
        BulkRetractFilter filter,
        Guid by,
        string reason,
        DateTimeOffset at,
        CancellationToken ct);
}
