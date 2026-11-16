using BuildingBlocks.Persistence;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingBlocks.Messaging;

/// <summary>Регистрация transactional outbox в composition root.</summary>
public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Добавляет модель <c>outbox_messages</c> в единый <see cref="MayakDbContext"/> и scoped
    /// <see cref="IOutbox"/>. Требует, чтобы
    /// <see cref="PersistenceServiceCollectionExtensions.AddPersistence"/> уже был вызван.
    /// </summary>
    /// <remarks>
    /// Диспетчер здесь не регистрируется: destination выбирается хостом
    /// (<c>AddOutboxDispatcher("api")</c> или <c>"worker"</c>), чтобы Api не обрабатывал
    /// сообщения воркера и наоборот.
    /// </remarks>
    public static IServiceCollection AddOutbox(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IModelContributor, OutboxModelContributor>();
        services.AddScoped<IOutbox, Outbox>();
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }

    /// <summary>
    /// Регистрирует фоновый <see cref="OutboxDispatcher"/> для одного destination.
    /// </summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="destination">Сторона-получатель: <c>api</c> или <c>worker</c>.</param>
    /// <param name="configure">Необязательная настройка интервала, пачки, попыток и backoff.</param>
    /// <param name="runInBackground">
    /// Запускать ли диспетчер как <see cref="IHostedService"/>. <c>false</c> оставляет
    /// singleton <see cref="OutboxDispatcher"/> в DI для ручного <see cref="OutboxDispatcher.DispatchOnceAsync"/>:
    /// так интеграционные тесты Api не зависят от фонового цикла и вызывают доставку сами.
    /// </param>
    public static IServiceCollection AddOutboxDispatcher(
        this IServiceCollection services,
        string destination,
        Action<OutboxDispatcherOptions>? configure = null,
        bool runInBackground = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var options = new OutboxDispatcherOptions { Destination = destination };
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<OutboxDispatcher>(provider => new OutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<TimeProvider>(),
            options,
            // Хост регистрирует логирование; в тестовом/ручном контейнере его может не быть.
            provider.GetService<ILogger<OutboxDispatcher>>() ?? NullLogger<OutboxDispatcher>.Instance));

        if (runInBackground)
        {
            services.AddHostedService<OutboxDispatcher>(
                provider => provider.GetRequiredService<OutboxDispatcher>());
        }

        return services;
    }

    /// <summary>Регистрирует обработчик сообщений outbox (scoped: может использовать БД модуля).</summary>
    public static IServiceCollection AddOutboxHandler<THandler>(this IServiceCollection services)
        where THandler : class, IOutboxHandler
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IOutboxHandler, THandler>();

        return services;
    }
}
