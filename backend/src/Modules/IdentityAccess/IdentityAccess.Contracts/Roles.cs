namespace IdentityAccess.Contracts;

/// <summary>
/// Базовые роли и наборы прав из матрицы. Роль — только именованный набор прав;
/// аноним ролью не является (его <c>map.read</c> выдаётся на уровне приложения).
/// </summary>
public static class Roles
{
    public const string User = "user";
    public const string Moderator = "moderator";
    public const string Admin = "admin";

    /// <summary>Права, которые есть только у администратора (модератор их не получает).</summary>
    private static readonly string[] AdminOnlyPermissions =
    [
        Permissions.SourceSecretsManage,
        Permissions.SettingsGlobalManage,
        Permissions.UsersRolesAssign,
        Permissions.AuditRead,
    ];

    private static readonly string[] UserPermissions =
    [
        Permissions.MapRead,
        Permissions.ObservationCreate,
    ];

    private static readonly string[] ModeratorPermissions =
    [
        .. Permissions.All.Where(permission => !AdminOnlyPermissions.Contains(permission, StringComparer.Ordinal)),
    ];

    private static readonly string[] AdminPermissions = [.. Permissions.All];

    private static readonly IReadOnlyCollection<string> UserRolePermissions = Array.AsReadOnly(UserPermissions);
    private static readonly IReadOnlyCollection<string> ModeratorRolePermissions = Array.AsReadOnly(ModeratorPermissions);
    private static readonly IReadOnlyCollection<string> AdminRolePermissions = Array.AsReadOnly(AdminPermissions);

    /// <summary>Все роли, которые существуют в БД (аноним сюда не входит).</summary>
    public static IReadOnlyCollection<string> AllRoles { get; } = Array.AsReadOnly(new[] { User, Moderator, Admin });

    /// <summary>
    /// Права роли по матрице: <c>user</c> — карта и отметки, <c>moderator</c> — всё,
    /// кроме админских прав, <c>admin</c> — все права.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Роль неизвестна.</exception>
    public static IReadOnlyCollection<string> PermissionsFor(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        return role switch
        {
            User => UserRolePermissions,
            Moderator => ModeratorRolePermissions,
            Admin => AdminRolePermissions,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Неизвестная роль."),
        };
    }
}
