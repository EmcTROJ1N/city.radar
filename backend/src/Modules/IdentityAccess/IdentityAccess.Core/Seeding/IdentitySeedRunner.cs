using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IdentityAccess.Core.Seeding;

/// <summary>
/// Запуск seed при старте приложения: роли и права создаются всегда, dev-пользователи —
/// только в окружениях <c>Development</c> и <c>Testing</c> (в проде dev-seed
/// не применяется). Каждый сидер получает собственный scope и <see cref="BuildingBlocks.Persistence.MayakDbContext"/>.
/// </summary>
public sealed class IdentitySeedRunner(IServiceProvider services, IHostEnvironment environment)
{
    /// <summary>Выполняет seed в отдельном DI-scope.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);

        await using var scope = services.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<RoleSeeder>().SeedAsync(ct);

        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            await scope.ServiceProvider.GetRequiredService<DevSeeder>().SeedAsync(ct);
        }
    }
}
