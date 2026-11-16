using IdentityAccess.Core.Auth;

using Microsoft.Extensions.DependencyInjection;

namespace Api.IntegrationTests.Identity;

/// <summary>
/// Тестовый вход: в продуктовом коде способа выдать токен без MAX нет, поэтому тесты
/// используют тот же <see cref="TokenService"/>, что и эндпоинты, с правами пользователя из БД.
/// </summary>
internal static class TestAuth
{
    public static async Task<string> CreateAccessTokenAsync(
        IServiceProvider services,
        Guid userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();

        var profiles = scope.ServiceProvider.GetRequiredService<UserProfileService>();
        var profile = await profiles.FindAsync(userId, ct)
            ?? throw new InvalidOperationException($"Пользователь {userId} не найден в БД.");

        var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();

        return tokens.IssueAccess(profile.Id, profile.DisplayName, profile.Permissions);
    }
}
