using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>Сборка, в которой лежат миграции схемы.</summary>
    private static readonly string MigrationsAssemblyName =
        typeof(MayakDbContext).Assembly.GetName().Name!;

    /// <summary>
    /// Регистрирует <see cref="MayakDbContext"/> на PostgreSQL + PostGIS со snake_case-именами
    /// и миграциями из сборки BuildingBlocks.Persistence.
    /// </summary>
    public static IServiceCollection AddPersistence(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<MayakDbContext>(options => options
            .UseNpgsql(connectionString, npgsql => npgsql
                .UseNetTopologySuite()
                .MigrationsAssembly(MigrationsAssemblyName))
            .UseSnakeCaseNamingConvention()
            // Модель собирается из IModelContributor модулей, поэтому кеш модели обязан
            // учитывать их состав (иначе тестовый fixture без contributors закешировал бы
            // пустую модель для всех контекстов процесса).
            .ReplaceService<IModelCacheKeyFactory, ContributorModelCacheKeyFactory>()
            // Модульные миграции (например, Identity) написаны вручную по db/schema.sql и не
            // обновляют ModelSnapshot. Расхождение модели с историей миграций ожидаемо и не
            // должно ронять старт приложения/тестов.
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)));

        return services;
    }
}
