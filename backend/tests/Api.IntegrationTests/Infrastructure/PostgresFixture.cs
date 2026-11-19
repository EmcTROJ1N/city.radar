using BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Testcontainers.PostgreSql;

namespace Api.IntegrationTests.Infrastructure;

/// <summary>
/// Один контейнер PostGIS на всю коллекцию тестов: поднимает БД и применяет миграции из
/// BuildingBlocks.Persistence, чтобы тесты проверяли именно ту схему, которая уедет в прод.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgis/postgis:16-3.4")
        .WithDatabase("mayak")
        .WithUsername("mayak")
        .WithPassword("mayak")
        .Build();

    private ServiceProvider? _services;

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        _services = new ServiceCollection()
            .AddPersistence(ConnectionString)
            .BuildServiceProvider();

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MayakDbContext>();

        await context.Database.MigrateAsync();

        // Тип фиксации обязателен. Тестам, которым тип не важен, — тип «любая геометрия»
        // в отдельной категории без источника «Сайт»: поведение как у фиксации без типа.
        await context.Database.ExecuteSqlRawAsync(
            $"""
             insert into categories (id, code, name)
             values ('{TestCategoryId}', 'test_untyped', 'Тестовая категория')
             on conflict (id) do nothing;
             insert into object_types (id, category_id, code, name, geometry_kind)
             values ('{AnyObjectTypeId}', '{TestCategoryId}', 'test_any', 'Тестовый тип', 'any')
             on conflict (id) do nothing;
             """);
    }

    /// <summary>Категория тестового типа; источника «Сайт» у неё нет.</summary>
    public const string TestCategoryId = "00000000-0000-0000-0000-00000000aa00";

    /// <summary>Тип «любая геометрия» для фиксаций, создаваемых тестами через API.</summary>
    public const string AnyObjectTypeId = "00000000-0000-0000-0000-00000000aa01";

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await _container.DisposeAsync();
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
