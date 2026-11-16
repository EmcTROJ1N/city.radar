using BuildingBlocks.Persistence;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Sources.Contracts;
using Sources.Core.Health;
using Sources.Core.Persistence;
using Sources.Core.Secrets;
using Sources.Core.Services;

namespace Sources.Core;

/// <summary>Регистрация модуля источников в composition root.</summary>
public static class SourcesServiceCollectionExtensions
{
    /// <summary>
    /// Добавляет модель источников в единый <see cref="MayakDbContext"/>, сервис источников
    /// и контракты для других модулей. Требует, чтобы
    /// <see cref="PersistenceServiceCollectionExtensions.AddPersistence"/> уже был вызван.
    /// </summary>
    public static IServiceCollection AddSources(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IModelContributor, SourcesModelContributor>();

        services.AddScoped<SourceService>();
        services.AddScoped<SourceStatusService>();
        services.AddScoped<SecretStore>();
        services.AddScoped<SecretValidationService>();
        services.AddScoped<ConnectionService>();

        // Health пишет Worker через контракт; в Api та же реализация нужна для DI-тестов
        // и для возможного переиспользования без отдельной регистрации в Host.
        services.AddSourceHealthReporter();

        // Контракты для других модулей: каталог проверяет существование источника,
        // модуль наблюдений позже заменит guard удаления (TryAdd — чтобы подмена была возможна).
        services.AddScoped<ISourceDirectory, SourceDirectory>();
        services.AddScoped<ISiteSourceProvisioner, SiteSourceProvisioner>();
        services.TryAddScoped<ISourceDeletionGuard, SourceDeletionGuard>();
        services.TryAddScoped<ISourceStatusDeletionGuard, SourceStatusDeletionGuard>();

        return services;
    }

    /// <summary>
    /// Регистрирует <see cref="ISourceHealthReporter"/> — запись health-check источника
    ///. Отдельное расширение позволяет Worker'у зарегистрировать отчёт
    /// в своём composition root без всего модуля источников.
    /// </summary>
    public static IServiceCollection AddSourceHealthReporter(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<ISourceHealthReporter, SourceHealthWriter>();

        return services;
    }
}
