using BuildingBlocks.Persistence;

using Catalog.Contracts;
using Catalog.Core.Persistence;
using Catalog.Core.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Catalog.Core;

/// <summary>Регистрация модуля каталога в composition root.</summary>
public static class CatalogServiceCollectionExtensions
{
    /// <summary>
    /// Добавляет модель каталога в единый <see cref="MayakDbContext"/>, сервис фиксаций и
    /// контракты для других модулей. Требует, чтобы
    /// <see cref="PersistenceServiceCollectionExtensions.AddPersistence"/> уже был вызван.
    /// </summary>
    public static IServiceCollection AddCatalog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IModelContributor, CatalogModelContributor>();

        services.AddScoped<MapObjectService>();

        // Контракты для других модулей: ядро наблюдений читает привязки источников,
        // модуль наблюдений позже заменит guard удаления (TryAdd — чтобы подмена была возможна).
        services.AddScoped<IMapObjectDirectory, MapObjectDirectory>();
        services.TryAddScoped<IMapObjectDeletionGuard, MapObjectDeletionGuard>();

        return services;
    }
}
