namespace IdentityAccess.Contracts;

/// <summary>
/// Текущий пользователь запроса: права берутся только из permission-claims JWT,
/// а не из имени роли. Используется domain-кодом для аудита и проверок, привязанных к актору.
/// </summary>
public interface ICurrentUser
{
    /// <summary>Идентификатор пользователя из claim <c>sub</c>; <c>null</c> для анонима.</summary>
    Guid? UserId { get; }

    /// <summary>Есть ли у пользователя указанное право.</summary>
    bool Has(string permission);

    /// <summary>Все права текущего пользователя из claim <c>permission</c>.</summary>
    IReadOnlyCollection<string> Permissions { get; }

    /// <summary>IP-адрес клиента; <c>null</c> вне HTTP-запроса.</summary>
    string? Ip { get; }

    /// <summary>Заголовок <c>User-Agent</c>; <c>null</c>, если он не передан.</summary>
    string? UserAgent { get; }
}
