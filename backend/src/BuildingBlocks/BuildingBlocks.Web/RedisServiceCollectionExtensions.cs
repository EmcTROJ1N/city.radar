using BuildingBlocks.Web.Caching;
using BuildingBlocks.Web.RateLimiting;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BuildingBlocks.Web;

/// <summary>
/// Регистрация Redis-инфраструктуры: кэш ответов карты
/// и rate limit. Redis — необязательная зависимость: без строки <c>ConnectionStrings:Redis</c>
/// регистрируются только реализации в памяти, приложение работает.
/// </summary>
public static class RedisServiceCollectionExtensions
{
    /// <summary>
    /// Добавляет <see cref="IRateLimiter"/>, <see cref="IResponseCache"/> и параметры лимитов.
    /// </summary>
    /// <remarks>
    /// Если <c>ConnectionStrings:Redis</c> задана, используются Redis-реализации; они сами
    /// деградируют при недоступности Redis (кэш — чтение из БД, лимит — счётчики процесса),
    /// поэтому отдельная проверка доступности на старте не нужна. Если строки нет,
    /// кэш не используется, а лимит считается в памяти процесса.
    /// </remarks>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="configuration">Конфигурация приложения.</param>
    public static IServiceCollection AddRedisInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<RedisConnection>();
        services.TryAddSingleton<InMemoryRateLimiter>();
        services.TryAddSingleton(RateLimitOptions.FromConfiguration(configuration));

        var redisConfigured = !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Redis"));

        // Replace, а не TryAdd: модуль карты мог зарегистрировать пустой кэш по умолчанию,
        // и порядок вызовов AddMapReadModel/AddRedisInfrastructure не должен влиять на результат.
        services.Replace(ServiceDescriptor.Singleton<IRateLimiter>(provider => redisConfigured
            ? ActivatorUtilities.CreateInstance<RedisRateLimiter>(provider)
            : provider.GetRequiredService<InMemoryRateLimiter>()));

        services.Replace(ServiceDescriptor.Singleton<IResponseCache>(provider => redisConfigured
            ? ActivatorUtilities.CreateInstance<RedisResponseCache>(provider)
            : new NullResponseCache()));

        return services;
    }
}
