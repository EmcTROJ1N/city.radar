using IdentityAccess.Contracts;

using Shouldly;

namespace Api.IntegrationTests.Identity;

/// <summary>Матрица прав в коде: коды прав и наборы прав ролей.</summary>
public sealed class PermissionsTests
{
    private static readonly string[] ExpectedAdminOnlyPermissions =
    [
        Permissions.SourceSecretsManage,
        Permissions.SettingsGlobalManage,
        Permissions.UsersRolesAssign,
        Permissions.AuditRead,
    ];

    private static readonly string[] ExpectedPermissions =
    [
        "map.read",
        "observation.create",
        "observation.retract",
        "catalog.category.manage",
        "catalog.objecttype.manage",
        "catalog.object.manage",
        "catalog.group.manage",
        "catalog.status.manage",
        "source.manage",
        "moderation.queue.process",
        "source.secrets.manage",
        "settings.global.manage",
        "users.roles.assign",
        "audit.read",
    ];

    [Fact]
    public void Permissions_all_contains_14_unique_codes()
    {
        Permissions.All.Count.ShouldBe(14);
        Permissions.All.Distinct(StringComparer.Ordinal).Count().ShouldBe(14);

        Permissions.All.ShouldContain(Permissions.MapRead);
        Permissions.All.ShouldBe(ExpectedPermissions, ignoreOrder: true);
    }

    [Fact]
    public void Moderator_has_no_admin_permissions()
    {
        var moderator = Roles.PermissionsFor(Roles.Moderator);

        foreach (var permission in ExpectedAdminOnlyPermissions)
        {
            moderator.ShouldNotContain(permission);
            Permissions.All.ShouldContain(permission, $"Право '{permission}' должно быть в общем списке.");
        }

        moderator.Count.ShouldBe(Permissions.All.Count - ExpectedAdminOnlyPermissions.Length);
    }

    [Fact]
    public void Admin_has_all_permissions()
    {
        Roles.PermissionsFor(Roles.Admin).ShouldBe(Permissions.All, ignoreOrder: true);
    }

    [Fact]
    public void User_has_only_map_read_and_observation_create()
    {
        Roles.PermissionsFor(Roles.User).ShouldBe(
            [Permissions.MapRead, Permissions.ObservationCreate],
            ignoreOrder: true);
    }

    [Fact]
    public void All_roles_are_user_moderator_admin()
    {
        Roles.AllRoles.ShouldBe([Roles.User, Roles.Moderator, Roles.Admin], ignoreOrder: true);
    }

    [Fact]
    public void Unknown_role_is_rejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Roles.PermissionsFor("anonymous"));
    }
}
