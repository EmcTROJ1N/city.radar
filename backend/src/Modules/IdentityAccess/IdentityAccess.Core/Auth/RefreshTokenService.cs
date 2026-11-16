using System.Security.Cryptography;
using System.Text;

using BuildingBlocks.Persistence;

using IdentityAccess.Core.Domain;

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace IdentityAccess.Core.Auth;

/// <summary>
/// Refresh-токены: сырой токен — 32 случайных байта в base64url, в БД лежит только
/// SHA-256 хеш. При ротации старый токен отзывается, выдаётся новый; повторное использование
/// отозванного токена считается компрометацией и отзывает все активные токены пользователя.
/// </summary>
public sealed class RefreshTokenService(MayakDbContext db, JwtOptions options, TimeProvider timeProvider)
{
    private const int RawTokenBytes = 32;

    /// <summary>Выдаёт новый refresh-токен пользователю.</summary>
    public async Task<(string RawToken, DateTimeOffset ExpiresAt)> IssueAsync(Guid userId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var expiresAt = now.Add(options.RefreshTokenLifetime);
        var rawToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(RawTokenBytes));

        // CreatedAt явно: default now() в БД не учитывает TimeProvider.
        db.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = ComputeHash(rawToken),
            CreatedAt = now,
            ExpiresAt = expiresAt,
        });

        await db.SaveChangesAsync(ct);

        return (rawToken, expiresAt);
    }

    /// <summary>
    /// Меняет активный refresh-токен на новый. Возвращает <c>null</c>, если токен неизвестен,
    /// истёк или уже отозван. При повторном использовании отозванного токена отзывает все
    /// активные токены пользователя.
    /// </summary>
    public async Task<(string NewToken, DateTimeOffset ExpiresAt, Guid UserId)?> RotateAsync(
        string rawToken,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);

        var now = timeProvider.GetUtcNow();
        var tokenHash = ComputeHash(rawToken);

        var token = await db.Set<RefreshToken>()
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.TokenHash == tokenHash, ct);

        if (token is null)
        {
            return null;
        }

        // Повтор уже использованного токена: возможна кража, отзываем всю семью токенов.
        if (token.RevokedAt is not null)
        {
            await RevokeFamilyAsync(token.UserId, now, ct);
            return null;
        }

        if (token.ExpiresAt <= now)
        {
            return null;
        }

        // Условный UPDATE защищает от гонки двух параллельных refresh с одним токеном:
        // успеет только тот, кто первым перевёл revoked_at из NULL.
        var revoked = await db.Set<RefreshToken>()
            .Where(candidate => candidate.Id == token.Id && candidate.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(candidate => candidate.RevokedAt, now),
                ct);

        if (revoked == 0)
        {
            return null;
        }

        var (newToken, expiresAt) = await IssueAsync(token.UserId, ct);

        return (newToken, expiresAt, token.UserId);
    }

    /// <summary>Отзывает refresh-токен по сырому значению; неизвестный токен — no-op.</summary>
    public async Task RevokeAsync(string rawToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var tokenHash = ComputeHash(rawToken);

        await db.Set<RefreshToken>()
            .Where(token => token.TokenHash == tokenHash && token.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(token => token.RevokedAt, now),
                ct);
    }

    /// <summary>Отзывает все активные refresh-токены пользователя (семья).</summary>
    private async Task RevokeFamilyAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await db.Set<RefreshToken>()
            .Where(token => token.UserId == userId && token.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(token => token.RevokedAt, now),
                ct);
    }

    private static byte[] ComputeHash(string rawToken) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
}
