namespace IdentityAccess.Core.Domain;

/// <summary>
/// Пользователь портала (таблица <c>users</c>). Вход только через мессенджер MAX,
/// поэтому пароля и e-mail нет — набора полей ASP.NET Core Identity достаточно.
/// </summary>
internal sealed class AppUser
{
    public Guid Id { get; set; }

    /// <summary>Identity-имя: строковое представление <see cref="MaxUserId"/>.</summary>
    public string UserName { get; set; } = null!;

    public string NormalizedUserName { get; set; } = null!;

    public string? SecurityStamp { get; set; }

    public string? ConcurrencyStamp { get; set; }

    public long MaxUserId { get; set; }

    public string DisplayName { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }
}
