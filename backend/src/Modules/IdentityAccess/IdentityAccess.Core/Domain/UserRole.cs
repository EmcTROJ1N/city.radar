namespace IdentityAccess.Core.Domain;

/// <summary>Связь пользователя с ролью (таблица <c>user_roles</c>).</summary>
internal sealed class UserRole
{
    public Guid UserId { get; set; }

    public Guid RoleId { get; set; }
}
