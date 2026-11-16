using Observations.Domain;

namespace Observations.Application;

/// <summary>
/// Запись проекции <c>current_statuses</c>. Реализация живёт в
/// <c>Observations.Infrastructure</c>: только она знает про <c>MayakDbContext</c>, транзакции и
/// advisory lock. Прикладной слой передаёт расчёт, а не готовый результат, потому что факты
/// обязаны читаться уже после взятия блокировки по фиксации.
/// </summary>
public interface ICurrentStatusWriter
{
    /// <summary>
    /// Берёт блокировку по фиксации, вычисляет итог переданным делегатом и делает upsert строки
    /// проекции. Если вызывающий уже открыл транзакцию <c>MayakDbContext</c>, работа идёт в ней
    /// (одна транзакция с наблюдением и аудитом —), иначе открывается собственная.
    /// </summary>
    /// <param name="mapObjectId">Фиксация, для которой пишется проекция.</param>
    /// <param name="now">Серверное время пересчёта: попадает в <c>updated_at</c> при изменении итога.</param>
    /// <param name="calculate">
    /// Расчёт итогового статуса; вызывается внутри транзакции после взятия advisory lock,
    /// поэтому читает уже зафиксированные и собственные (ещё не сохранённые) наблюдения.
    /// </param>
    /// <param name="ct">Токен отмены.</param>
    Task WriteAsync(
        Guid mapObjectId,
        DateTimeOffset now,
        Func<CancellationToken, Task<CurrentStatusView>> calculate,
        CancellationToken ct);
}
