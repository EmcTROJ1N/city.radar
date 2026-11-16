using BuildingBlocks.Persistence;

using IdentityAccess.Contracts;
using IdentityAccess.Core.Domain;

using Microsoft.EntityFrameworkCore;

namespace IdentityAccess.Core.Seeding;

/// <summary>
/// Идемпотентный seed ролей <c>user</c>, <c>moderator</c>, <c>admin</c> и их прав
/// (<c>role_claims.claim_type = 'permission'</c>). Источник правды — <see cref="Roles.PermissionsFor(string)"/>
/// (матрица, сверена с <c>db/seed.sql</c>): недостающие claims добавляются, лишние
/// permission-claims удаляются, поэтому повторный запуск приводит БД к эталону.
/// </summary>
public sealed class RoleSeeder(MayakDbContext db)
{
    private const string PermissionClaimType = "permission";

    /// <summary>Приводит роли и их права к эталону из кода. Повторный вызов ничего не дублирует.</summary>
    public async Task SeedAsync(CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var roles = await db.Set<AppRole>().ToListAsync(ct);
        var roleIds = roles.Select(role => role.Id).ToArray();
        var existingClaims = roleIds.Length == 0
            ? new List<RoleClaim>()
            : await db.Set<RoleClaim>()
                .Where(claim => roleIds.Contains(claim.RoleId))
                .ToListAsync(ct);

        foreach (var roleName in Roles.AllRoles)
        {
            var normalizedName = Normalize(roleName);
            var role = roles.FirstOrDefault(candidate => string.Equals(
                    candidate.NormalizedName,
                    normalizedName,
                    StringComparison.Ordinal))
                // Запасной поиск на случай роли, заведённой вручную с другим normalized_name.
                ?? roles.FirstOrDefault(candidate => string.Equals(
                    candidate.Name,
                    roleName,
                    StringComparison.OrdinalIgnoreCase));

            if (role is null)
            {
                role = new AppRole
                {
                    Id = Guid.NewGuid(),
                    Name = roleName,
                    NormalizedName = normalizedName,
                };
                db.Add(role);
                roles.Add(role);
            }
            else
            {
                // Имя роли — часть эталона: правим регистр, если в БД он отличается.
                role.Name = roleName;
                role.NormalizedName = normalizedName;
            }

            ReconcilePermissionClaims(role, Roles.PermissionsFor(roleName), existingClaims);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Добавляет недостающие permission-claims роли и удаляет лишние. Claims других типов
    /// (не <c>permission</c>) не трогаем — их может добавить кто-то ещё.
    /// </summary>
    private void ReconcilePermissionClaims(
        AppRole role,
        IReadOnlyCollection<string> permissions,
        List<RoleClaim> existingClaims)
    {
        var permissionClaims = existingClaims
            .Where(claim => claim.RoleId == role.Id
                && string.Equals(claim.ClaimType, PermissionClaimType, StringComparison.Ordinal))
            .ToList();

        var target = new HashSet<string>(permissions, StringComparer.Ordinal);

        foreach (var extra in permissionClaims.Where(claim => !target.Contains(claim.ClaimValue)).ToList())
        {
            db.Remove(extra);
            existingClaims.Remove(extra);
            permissionClaims.Remove(extra);
        }

        var present = permissionClaims
            .Select(claim => claim.ClaimValue)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var permission in target.Where(permission => !present.Contains(permission)))
        {
            var claim = new RoleClaim
            {
                RoleId = role.Id,
                ClaimType = PermissionClaimType,
                ClaimValue = permission,
            };
            db.Add(claim);
            existingClaims.Add(claim);
        }
    }

    private static string Normalize(string roleName) => roleName.ToUpperInvariant();
}
