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
    }

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
