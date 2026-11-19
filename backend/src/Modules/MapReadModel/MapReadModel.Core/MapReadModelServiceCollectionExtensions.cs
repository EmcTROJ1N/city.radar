using BuildingBlocks.Web.Caching;

using MapReadModel.Core.Geocoding;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MapReadModel.Core;

/// <summary>Регистрация модуля карты в composition root.</summary>
public static class MapReadModelServiceCollectionExtensions
{
    /// <summary>
    /// Добавляет read-only выборку карты. Требует, чтобы persistence уже был зарегистрирован
    /// (<c>AddPersistence</c>): модуль читает <c>map_objects</c>/<c>current_statuses</c> сырым SQL
    /// через <see cref="BuildingBlocks.Persistence.MayakDbContext"/> и не создаёт таблиц.
    /// </summary>
    /// <remarks>
    /// <see cref="IResponseCache"/> по умолчанию — пустой кэш: <c>AddRedisInfrastructure</c>
    /// заменяет его на Redis-реализацию, если Redis настроен, поэтому карта работает и без него.
    /// </remarks>
    public static IServiceCollection AddMapReadModel(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IResponseCache, NullResponseCache>();

        services.AddScoped<MapQueries>();
        services.AddScoped<MapFeaturesService>();
        services.AddScoped<ObjectCardQuery>();
        services.AddScoped<MapCache>();
        services.AddScoped<ZonePointsQuery>();

        // Геокодер — синглтон: общий на процесс лимит «1 запрос в секунду» и кэш в памяти.
        services.TryAddSingleton(provider =>
            GeocodingOptions.FromConfiguration(provider.GetRequiredService<IConfiguration>()));
        services.TryAddSingleton<IGeocoder>(provider => new NominatimGeocoder(
            provider.GetRequiredService<GeocodingOptions>(),
            provider.GetRequiredService<IResponseCache>(),
            provider.GetRequiredService<TimeProvider>()));

        // Версия состояния карты — общий на процесс кэш на 1 секунду: провайдер
        // синглтон, поэтому ему нужен scope factory для получения scoped MayakDbContext.
        services.AddSingleton<IMapVersionSource, MapVersionSource>();
        services.AddSingleton<MapVersionProvider>();

        return services;
    }
}
