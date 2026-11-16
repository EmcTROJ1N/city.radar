namespace Sources.Contracts;

/// <summary>
/// Проверка «на статус источника есть наблюдения» перед удалением:
/// статус, по которому есть наблюдения, удалять нельзя — источник снимают с публикации
/// отключением, а историю наблюдений не переписывают.
/// </summary>
/// <remarks>
/// Таблицы <c>observations</c> на этапе источников ещё нет (она создаётся позже),
/// поэтому интерфейс выделен отдельно: модуль наблюдений заменит реализацию по
/// умолчанию, и удаление начнёт возвращать 409 без изменения кода источников — по образцу
/// <see cref="ISourceDeletionGuard"/>.
/// </remarks>
public interface ISourceStatusDeletionGuard
{
    /// <summary><c>true</c>, если по статусу есть хотя бы одно наблюдение.</summary>
    Task<bool> HasObservationsAsync(Guid sourceId, Guid sourceStatusId, CancellationToken ct);
}
