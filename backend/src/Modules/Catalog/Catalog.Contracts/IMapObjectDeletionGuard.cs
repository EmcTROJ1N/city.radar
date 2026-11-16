namespace Catalog.Contracts;

/// <summary>
/// Проверка «у фиксации есть история наблюдений» перед удалением:
/// фиксацию с наблюдениями удалять нельзя — её деактивируют (<c>isActive: false</c>).
/// </summary>
/// <remarks>
/// Таблицы <c>observations</c> на этапе каталога ещё нет (она создаётся позже), поэтому
/// интерфейс выделен отдельно: модуль наблюдений заменит реализацию по умолчанию,
/// и удаление начнёт возвращать 409 без изменения кода каталога.
/// </remarks>
public interface IMapObjectDeletionGuard
{
    /// <summary><c>true</c>, если у фиксации есть хотя бы одно наблюдение.</summary>
    Task<bool> HasObservationsAsync(Guid mapObjectId, CancellationToken ct);
}
