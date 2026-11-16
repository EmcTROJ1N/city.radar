using BuildingBlocks.Persistence;

using IdentityAccess.Core.Domain;

using Microsoft.EntityFrameworkCore;

namespace IdentityAccess.Core.Auth;

/// <summary>Профиль пользователя для <c>GET /me</c> и выдачи access-токена.</summary>
public sealed record UserProfile(
    Guid Id,
    string DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);

/// <summary>
/// Читает пользователя, его роли и права (role_claims всех ролей, без дублей, по алфавиту).
/// Роли нужны только для ответа <c>/me</c>: авторизация строится исключительно на правах.
/// </summary>
public sealed class UserProfileService(MayakDbContext db)
{
    /// <summary>
    /// Возвращает профиль либо <c>null</c>, если пользователь не существует (например, удалён
    /// после выдачи токена).
    /// </summary>
    public async Task<UserProfile?> FindAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Set<AppUser>()
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == userId, ct);

        return user is null ? null : await BuildAsync(user, ct);
    }

    /// <summary>
    /// Возвращает профиль по <c>user_name</c> (строковое представление MAX-id) либо <c>null</c>.
    /// Нужен dev-входу: под dev-паролем можно войти любым пользователем из таблицы
    /// <c>users</c>, в том числе добавленным в БД вручную.
    /// </summary>
    public async Task<UserProfile?> FindByUserNameAsync(string userName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        var user = await db.Set<AppUser>()
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.UserName == userName, ct);

        return user is null ? null : await BuildAsync(user, ct);
    }

    /// <summary>Дочитывает роли и права найденного пользователя.</summary>
    private async Task<UserProfile> BuildAsync(AppUser user, CancellationToken ct)
    {
        var userId = user.Id;

        var roleNames = await (
                from link in db.Set<UserRole>()
                join role in db.Set<AppRole>() on link.RoleId equals role.Id
                where link.UserId == userId
                select role.Name)
            .Distinct()
            .ToListAsync(ct);

        var permissions = await (
                from link in db.Set<UserRole>()
                join claim in db.Set<RoleClaim>() on link.RoleId equals claim.RoleId
                where link.UserId == userId
                    && claim.ClaimType == HttpContextCurrentUser.PermissionClaimType
                select claim.ClaimValue)
            .Distinct()
            .ToListAsync(ct);

        roleNames.Sort(StringComparer.Ordinal);
        permissions.Sort(StringComparer.Ordinal);

        return new UserProfile(user.Id, user.DisplayName, roleNames, permissions);
    }
}
