namespace IdentityAccess.Contracts;

/// <summary>
/// Коды прав из матрицы. Доступ проверяется только по этим кодам (policy-based
/// authorization), а не по именам ролей: промежуточные роли можно вводить без изменения кода.
/// </summary>
public static class Permissions
{
    public const string MapRead = "map.read";
    public const string ObservationCreate = "observation.create";
    public const string ObservationRetract = "observation.retract";
    public const string CatalogCategoryManage = "catalog.category.manage";
    public const string CatalogObjectTypeManage = "catalog.objecttype.manage";
    public const string CatalogObjectManage = "catalog.object.manage";
    public const string CatalogGroupManage = "catalog.group.manage";
    public const string CatalogStatusManage = "catalog.status.manage";
    public const string SourceManage = "source.manage";
    public const string ModerationQueueProcess = "moderation.queue.process";
    public const string SourceSecretsManage = "source.secrets.manage";
    public const string SettingsGlobalManage = "settings.global.manage";
    public const string UsersRolesAssign = "users.roles.assign";
    public const string AuditRead = "audit.read";

    private static readonly string[] AllCodes =
    [
        MapRead,
        ObservationCreate,
        ObservationRetract,
        CatalogCategoryManage,
        CatalogObjectTypeManage,
        CatalogObjectManage,
        CatalogGroupManage,
        CatalogStatusManage,
        SourceManage,
        ModerationQueueProcess,
        SourceSecretsManage,
        SettingsGlobalManage,
        UsersRolesAssign,
        AuditRead,
    ];

    /// <summary>Все коды прав: по одному policy на каждое право.</summary>
    public static IReadOnlyCollection<string> All { get; } = Array.AsReadOnly(AllCodes);
}
