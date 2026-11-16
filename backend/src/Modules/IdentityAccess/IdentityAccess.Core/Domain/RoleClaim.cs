namespace IdentityAccess.Core.Domain;

/// <summary>
/// Право роли (таблица <c>role_claims</c>): <c>claim_type = 'permission'</c>,
/// <c>claim_value</c> — код из <see cref="IdentityAccess.Contracts.Permissions"/>.
/// </summary>
internal sealed class RoleClaim
{
    public int Id { get; set; }

    public Guid RoleId { get; set; }

    public string ClaimType { get; set; } = null!;

    public string ClaimValue { get; set; } = null!;
}
