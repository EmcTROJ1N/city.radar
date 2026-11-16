namespace IdentityAccess.Contracts;

/// <summary>
/// Журнал аудита: кто, когда, что и над каким объектом сделал, значения «до/после»
/// и причина. Запись добавляется в текущий (scoped) <c>MayakDbContext</c> и сохраняется тем же
/// <c>SaveChanges</c>, что и бизнес-изменение вызывающего кода, — атомарно, в одной транзакции.
/// Журнал append-only: UPDATE и DELETE запрещены триггером БД.
/// </summary>
public interface IAuditLog
{
    /// <summary>
    /// Добавляет запись аудита. Метод не вызывает <c>SaveChanges</c>: вызывающий код сохраняет
    /// запись вместе со своим изменением (или она откатывается вместе с ним).
    /// </summary>
    /// <param name="action">Код действия, например <c>user.roles.assign</c>.</param>
    /// <param name="entityType">Тип объекта, например <c>user</c>; <c>null</c> для действий без объекта.</param>
    /// <param name="entityId">Идентификатор объекта в виде строки; <c>null</c>, если объекта нет.</param>
    /// <param name="before">Состояние «до»; сериализуется в jsonb (camelCase).</param>
    /// <param name="after">Состояние «после»; сериализуется в jsonb (camelCase).</param>
    /// <param name="reason">Причина действия, если она требуется.</param>
    /// <param name="ct">Токен отмены.</param>
    Task WriteAsync(
        string action,
        string? entityType,
        string? entityId,
        object? before,
        object? after,
        string? reason = null,
        CancellationToken ct = default);
}
