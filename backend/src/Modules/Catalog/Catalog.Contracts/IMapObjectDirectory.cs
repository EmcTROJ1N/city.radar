namespace Catalog.Contracts;

/// <summary>
/// Чтение привязок фиксаций к источникам другими модулями:
/// ядро наблюдений проверяет, что источник привязан к фиксации, а модуль источников — какие
/// фиксации обслуживает источник. Прямая ссылка на <c>Catalog.Core</c> запрещена.
/// </summary>
public interface IMapObjectDirectory
{
    /// <summary>Идентификаторы источников, привязанных к фиксации; пустой список — привязок нет.</summary>
    Task<IReadOnlyList<Guid>> GetSourceIdsAsync(Guid mapObjectId, CancellationToken ct);

    /// <summary>Фиксации, привязанные к источнику (краткая карточка для выбора модератором).</summary>
    Task<IReadOnlyList<LinkedObject>> GetObjectsOfSourceAsync(Guid sourceId, CancellationToken ct);

    /// <summary>Существует ли фиксация; удалённая фиксация не существует.</summary>
    Task<bool> ExistsAsync(Guid mapObjectId, CancellationToken ct);

    /// <summary>
    /// Активность и группа фиксации: ядро наблюдений не даёт отметить неактивную фиксацию
    /// (422) и кладёт <c>groupId</c> в событие outbox для детектора аномалий. <c>null</c> —
    /// фиксации нет (404).
    /// </summary>
    Task<MapObjectInfo?> GetInfoAsync(Guid mapObjectId, CancellationToken ct);
}

/// <summary>Активность и группа фиксации для проверок модуля наблюдений.</summary>
/// <param name="Id">Идентификатор фиксации.</param>
/// <param name="IsActive">Показывается ли фиксация на карте и доступна ли для отметок.</param>
/// <param name="GroupId">Группа фиксации; <c>null</c>, если не задана.</param>
/// <param name="CategoryId">
/// Категория фиксации (через тип объекта); <c>null</c> у фиксации без типа. По ней обработчик
/// отметок находит неявный источник «Сайт».
/// </param>
public sealed record MapObjectInfo(Guid Id, bool IsActive, Guid? GroupId, Guid? CategoryId);

/// <summary>Краткая карточка фиксации для списков привязки источников.</summary>
/// <param name="Id">Идентификатор фиксации.</param>
/// <param name="Name">Название фиксации.</param>
/// <param name="Address">Адрес; <c>null</c>, если не задан.</param>
public sealed record LinkedObject(Guid Id, string Name, string? Address);
