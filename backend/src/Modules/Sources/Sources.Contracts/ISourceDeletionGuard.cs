namespace Sources.Contracts;

/// <summary>
/// Проверка «у источника есть история наблюдений» перед удалением
///): источник с наблюдениями удалять нельзя — его отключают через
/// <c>POST /sources/{id}/disable</c>.
/// </summary>
/// <remarks>
/// Таблицы <c>observations</c> на этапе источников ещё нет (она создаётся позже), поэтому
/// интерфейс выделен отдельно: модуль наблюдений заменит реализацию по умолчанию,
/// и удаление начнёт возвращать 409 без изменения кода источников — по образцу
/// <c>IMapObjectDeletionGuard</c> в каталоге.
/// </remarks>
public interface ISourceDeletionGuard
{
    /// <summary><c>true</c>, если у источника есть хотя бы одно наблюдение.</summary>
    Task<bool> HasObservationsAsync(Guid sourceId, CancellationToken ct);
}
