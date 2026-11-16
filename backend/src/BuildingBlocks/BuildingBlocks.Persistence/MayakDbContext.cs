using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BuildingBlocks.Persistence;

/// <summary>
/// Единый контекст портала: одна схема, одна история миграций.
/// Модель собирается из <see cref="IModelContributor"/> модулей.
/// </summary>
public sealed class MayakDbContext(
    DbContextOptions<MayakDbContext> options,
    IEnumerable<IModelContributor> contributors)
    : DbContext(options)
{
    /// <summary>
    /// Состав contributors участвует в ключе кеша модели (см. <see cref="ContributorModelCacheKeyFactory"/>):
    /// иначе первый созданный контекст с другим набором contributors (например, в тестовом
    /// fixture) закешировал бы модель без сущностей модулей.
    /// </summary>
    internal string ContributorsCacheKey { get; } = string.Join(
        '|',
        contributors
            .Select(contributor => contributor.GetType().FullName)
            .Order(StringComparer.Ordinal));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        foreach (var contributor in contributors)
        {
            contributor.Configure(modelBuilder);
        }
    }
}

/// <summary>
/// Ключ кеша модели EF Core, учитывающий набор <see cref="IModelContributor"/>. По умолчанию
/// кеш привязан только к типу контекста, а модель портала собирается из модулей.
/// </summary>
internal sealed class ContributorModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        var modelContext = (MayakDbContext)context;

        return (modelContext.GetType(), designTime, modelContext.ContributorsCacheKey);
    }
}
