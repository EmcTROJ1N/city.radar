namespace IdentityAccess.Core.Domain;

/// <summary>
/// Refresh-токен (таблица <c>refresh_tokens</c>). В БД лежит только SHA-256 хеш токена.
/// </summary>
internal sealed class RefreshToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public byte[] TokenHash { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}
