using BuildingBlocks.Messaging;
using BuildingBlocks.Persistence;

using Catalog.Contracts;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Observations.Application;
using Observations.Application.Handlers;
using Observations.Contracts;

using Sources.Contracts;

namespace Observations.Infrastructure;

/// <summary>Регистрация модуля наблюдений в composition root.</summary>
public static class ObservationsServiceCollectionExtensions
{
    /// <summary>
    /// Добавляет модель наблюдений в единый <see cref="MayakDbContext"/>, хранилище, пересчёт
    /// проекции <c>current_statuses</c>, фоновый пересчёт по TTL и реальные проверки
    /// удаления источника/статуса/фиксации. Требует, чтобы
    /// <see cref="PersistenceServiceCollectionExtensions.AddPersistence"/>,
    /// <c>AddCatalog</c>, <c>AddSources</c> и <c>AddIdentityAccess</c> (для <c>IGlobalSettings</c>)
    /// уже были вызваны.
    /// </summary>
    /// <remarks>
    /// Guard'ы регистрируются последними через <c>AddScoped</c> поверх <c>TryAddScoped</c>-заглушек
    /// модулей источников и каталога: для <see cref="IServiceProvider"/> побеждает последняя
    /// регистрация, поэтому удаление с наблюдениями начнёт возвращать 409 без правок этих модулей.
    /// <para>
    /// Интервал и автозапуск <see cref="ExpirationSweeper"/> читаются из конфигурации процесса
    /// (<c>Observations:SweepIntervalSeconds</c>, <c>Observations:SweeperEnabled</c>); в тестовых
    /// хостах автозапуск выключается, и тесты вызывают <see cref="ExpirationSweeper.SweepOnceAsync"/>.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddObservations(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IModelContributor, ObservationsModelContributor>();

        services.AddScoped<IObservationRepository, ObservationRepository>();
        services.AddScoped<ICurrentStatusWriter, CurrentStatusWriter>();
        services.AddScoped<IStatusRecalculator, StatusRecalculator>();
        services.AddScoped<IObservationReadStore, ObservationReadStore>();
        services.AddScoped<ObservationQueries>();
        services.AddScoped<CreateObservationHandler>();
        services.AddScoped<RetractObservationHandler>();
        services.AddScoped<RetractBulkHandler>();
        services.AddScoped<IObservationFactory, ObservationFactory>();

        // Приём предложений Worker-а: идемпотентен по id сообщения outbox.
        services.AddOutboxHandler<ObservationProposedHandler>();

        services.AddScoped<ISourceDeletionGuard, SourceObservationDeletionGuard>();
        services.AddScoped<ISourceStatusDeletionGuard, SourceStatusObservationDeletionGuard>();
        services.AddScoped<IMapObjectDeletionGuard, MapObjectObservationDeletionGuard>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(provider =>
        {
            var options = new ExpirationSweeperOptions();
            options.Apply(provider.GetService<IConfiguration>());

            return options;
        });
        services.AddSingleton(provider => new ExpirationSweeper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ExpirationSweeperOptions>(),
            // Хост регистрирует логирование; в тестовом/ручном контейнере его может не быть.
            provider.GetService<ILogger<ExpirationSweeper>>() ?? NullLogger<ExpirationSweeper>.Instance));
        services.AddHostedService(provider => provider.GetRequiredService<ExpirationSweeper>());

        return services;
    }
}
