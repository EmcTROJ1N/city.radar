using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace IdentityAccess.Core.Auth;

/// <summary>
/// Настройки JWT (секция <c>Jwt</c>). Ключ подписи HS256 — не короче 32 байт (256 бит).
/// В Development/Testing при отсутствии ключа генерируется случайный ключ на процесс;
/// в Production отсутствие ключа — ошибка старта, а не молчаливая подмена.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    private const int MinimumSigningKeyBytes = 32;
    private const int GeneratedSigningKeyBytes = 48;

    public string Issuer { get; init; } = "mayak";

    public string Audience { get; init; } = "mayak";

    public string SigningKey { get; init; } = string.Empty;

    public int AccessTokenMinutes { get; init; } = 15;

    public int RefreshTokenDays { get; init; } = 30;

    /// <summary>Срок жизни access-токена (<c>Jwt:AccessTokenMinutes</c>, по умолчанию 15 минут).</summary>
    public TimeSpan AccessTokenLifetime => TimeSpan.FromMinutes(AccessTokenMinutes);

    /// <summary>Срок жизни refresh-токена (<c>Jwt:RefreshTokenDays</c>, по умолчанию 30 дней).</summary>
    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(RefreshTokenDays);

    /// <summary>Ключ подписи в виде, пригодном для <see cref="SigningCredentials"/>.</summary>
    public SymmetricSecurityKey CreateSigningKey() =>
        new(Encoding.UTF8.GetBytes(SigningKey));

    /// <summary>
    /// Читает и валидирует секцию <c>Jwt</c>. В Production без <c>Jwt:SigningKey</c> бросает
    /// <see cref="InvalidOperationException"/>, поэтому приложение не стартует с дефолтным ключом.
    /// </summary>
    public static JwtOptions Load(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var section = configuration.GetSection(SectionName);

        var signingKey = section["SigningKey"];
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            if (environment.IsProduction())
            {
                throw new InvalidOperationException(
                    "Не задан Jwt:SigningKey. В Production ключ подписи обязателен (минимум 32 байта).");
            }

            // Случайный ключ на процесс: перезапуск делает ранее выданные токены недействительными.
            signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(GeneratedSigningKeyBytes));
        }

        if (Encoding.UTF8.GetByteCount(signingKey) < MinimumSigningKeyBytes)
        {
            throw new InvalidOperationException(
                $"Jwt:SigningKey должен быть не короче {MinimumSigningKeyBytes} байт (HS256).");
        }

        var accessTokenMinutes = section.GetValue("AccessTokenMinutes", 15);
        var refreshTokenDays = section.GetValue("RefreshTokenDays", 30);

        if (accessTokenMinutes <= 0)
        {
            throw new InvalidOperationException("Jwt:AccessTokenMinutes должен быть больше нуля.");
        }

        if (refreshTokenDays <= 0)
        {
            throw new InvalidOperationException("Jwt:RefreshTokenDays должен быть больше нуля.");
        }

        return new JwtOptions
        {
            Issuer = section["Issuer"] ?? "mayak",
            Audience = section["Audience"] ?? "mayak",
            SigningKey = signingKey,
            AccessTokenMinutes = accessTokenMinutes,
            RefreshTokenDays = refreshTokenDays,
        };
    }
}
