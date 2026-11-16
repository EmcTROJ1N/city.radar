namespace IdentityAccess.Core.Domain;

/// <summary>Роль (таблица <c>roles</c>): <c>user</c>, <c>moderator</c>, <c>admin</c>.</summary>
internal sealed class AppRole
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    public string NormalizedName { get; set; } = null!;

    public string? ConcurrencyStamp { get; set; }
}
