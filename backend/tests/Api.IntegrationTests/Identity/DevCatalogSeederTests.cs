using System.Globalization;

using Api.Host.Seeding;
using Api.IntegrationTests.Infrastructure;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Shouldly;

namespace Api.IntegrationTests.Identity;

/// <summary>
/// Dev-seed каталога, источника и фиксации из <c>db/seed_dev.sql</c>:
/// при старте хоста в окружении <c>Testing</c> есть категория «Вода», источник с двумя
/// статусами ok/none и одна фиксация с привязкой к источнику; повторный запуск ничего
/// не дублирует. Демо-фиксация и источник, удалённые через интерфейс, не возвращаются:
/// журнал аудита хранит факт удаления. Сид живёт в composition root — <see cref="DevCatalogSeeder"/>.
/// </summary>
[Collection("postgres")]
public sealed class DevCatalogSeederTests(PostgresFixture fixture)
{
    private static readonly Guid WaterCategoryId = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid WaterSourceId = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
    private static readonly Guid WaterObjectId = Guid.Parse("00000000-0000-0000-0000-0000000000f1");

    private readonly SeedQueries _db = new(fixture);

    [Fact]
    public async Task Testing_host_seeds_catalog_and_source_idempotently()
    {
        var ct = CancellationToken.None;

        using var factory = new TestApiFactory(fixture.ConnectionString);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });

        // Старт хоста применяет миграции и запускает IdentitySeedRunner + DevCatalogSeeder.
        using (var health = await client.GetAsync("/health", ct))
        {
            health.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        }

        await AssertSeedAsync(ct);

        // Повторный запуск сида не дублирует записи.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DevCatalogSeeder>().SeedAsync(ct);
        }

        await AssertSeedAsync(ct);
    }

    [Fact]
    public async Task Deleted_seeded_object_is_not_resurrected()
    {
        var ct = CancellationToken.None;

        using var factory = new TestApiFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        using (var health = await client.GetAsync("/health", ct))
        {
            health.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        }

        try
        {
            // Удаление через интерфейс: строки нет, в append-only журнале — запись.
            await _db.ExecuteAsync($"delete from map_objects where id = '{WaterObjectId}'", ct);
            await _db.ExecuteAsync(
                $"""
                 insert into audit_entries (action, entity_type, entity_id)
                 values ('catalog.object.delete', 'map_object', '{WaterObjectId}')
                 """,
                ct);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<DevCatalogSeeder>().SeedAsync(ct);
            }

            (await _db.CountAsync(
                $"select count(*) from map_objects where id = '{WaterObjectId}'",
                ct)).ShouldBe(0L);
        }
        finally
        {
            // Общая БД коллекции: убираем искусственную запись журнала и возвращаем сид,
            // чтобы следующие тесты видели демо-фиксацию. TRUNCATE не вызывает триггер
            // запрета DELETE (так же чистит журнал SeedTests).
            await _db.ExecuteAsync("truncate table audit_entries", ct);

            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DevCatalogSeeder>().SeedAsync(ct);
        }
    }

    private async Task AssertSeedAsync(CancellationToken ct)
    {
        (await _db.CountAsync(
            $"select count(*) from categories where id = '{WaterCategoryId}' and code = 'water'",
            ct)).ShouldBe(1L);
        (await _db.CountAsync(
            $"select count(*) from object_types where id = '00000000-0000-0000-0000-0000000000b1'",
            ct)).ShouldBe(1L);
        (await _db.CountAsync(
            "select count(*) from status_definitions where code in ('ok', 'none')",
            ct)).ShouldBe(2L);
        (await _db.CountAsync(
            $"select count(*) from sources where id = '{WaterSourceId}'",
            ct)).ShouldBe(1L);
        (await _db.CountAsync(
            $"select count(*) from source_policies where source_id = '{WaterSourceId}'",
            ct)).ShouldBe(1L);
        (await _db.CountAsync(
            $"""
             select count(*) from source_statuses
             where source_id = '{WaterSourceId}' and code in ('ok', 'none')
             """,
            ct)).ShouldBe(2L);
        (await _db.CountAsync(
            $"select count(*) from map_objects where id = '{WaterObjectId}'",
            ct)).ShouldBe(1L);
        (await _db.CountAsync(
            $"""
             select count(*) from map_object_sources
             where map_object_id = '{WaterObjectId}' and source_id = '{WaterSourceId}'
             """,
            ct)).ShouldBe(1L);
    }

    /// <summary>Прямые запросы к общей БД коллекции: хост и его API здесь не нужны.</summary>
    private sealed class SeedQueries(PostgresFixture fixture)
    {
        public async Task<long> CountAsync(string sql, CancellationToken ct)
        {
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync(ct);

            await using var command = new NpgsqlCommand(sql, connection);

            return Convert.ToInt64(
                await command.ExecuteScalarAsync(ct),
                CultureInfo.InvariantCulture);
        }

        public async Task ExecuteAsync(string sql, CancellationToken ct)
        {
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync(ct);

            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(ct);
        }
    }
}
