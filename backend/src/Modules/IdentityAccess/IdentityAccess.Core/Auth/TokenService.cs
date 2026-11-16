using IdentityAccess.Core.Domain;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace IdentityAccess.Core.Auth;

/// <summary>
/// Выдаёт короткоживущие access-токены (JWT HS256): <c>sub</c> — идентификатор
/// пользователя, <c>name</c> — отображаемое имя, по одному claim <c>permission</c> на право.
/// Проверка доступа выполняется только по этим claim (policy-based authorization).
/// </summary>
public sealed class TokenService(JwtOptions options, TimeProvider timeProvider)
{
    private const string SubjectClaimType = "sub";
    private const string NameClaimType = "name";
    private const string JwtIdClaimType = "jti";
    private const string PermissionClaimType = "permission";

    private static readonly JsonWebTokenHandler Handler = new();

    /// <summary>Срок жизни access-токена; в ответе <c>/auth/refresh</c> отдаётся как <c>expiresIn</c>.</summary>
    public TimeSpan AccessTokenLifetime => options.AccessTokenLifetime;

    /// <summary>Выпускает access-токен для пользователя из БД.</summary>
    internal string IssueAccess(AppUser user, IReadOnlyCollection<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(user);

        return IssueAccess(user.Id, user.DisplayName, permissions);
    }

    /// <summary>Выпускает access-токен для субъекта без загрузки сущности (тесты, сервисные сценарии).</summary>
    public string IssueAccess(Guid userId, string displayName, IReadOnlyCollection<string> permissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(permissions);

        var now = timeProvider.GetUtcNow();

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [SubjectClaimType] = userId.ToString(),
            [NameClaimType] = displayName,
            [JwtIdClaimType] = Guid.NewGuid().ToString("N"),
            // Один claim на право: политики проверяют RequireClaim("permission", p).
            [PermissionClaimType] = permissions.Distinct(StringComparer.Ordinal).ToArray(),
        };

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.Add(options.AccessTokenLifetime).UtcDateTime,
            SigningCredentials = new SigningCredentials(
                options.CreateSigningKey(),
                SecurityAlgorithms.HmacSha256),
            Claims = claims,
        };

        return Handler.CreateToken(descriptor);
    }
}
