using Api.IntegrationTests.Infrastructure;

using Npgsql;
using Shouldly;

namespace Api.IntegrationTests;

[Collection("postgres")]
public sealed class MigrationsTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Applies_cleanly_on_empty_database()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var versionCommand = new NpgsqlCommand("select postgis_version()", connection);
        var version = (string?)await versionCommand.ExecuteScalarAsync();

        version.ShouldNotBeNullOrWhiteSpace();

        await using var extensionsCommand = new NpgsqlCommand(
            "select count(*) from pg_extension where extname in ('postgis', 'pg_trgm')",
            connection);
        var extensions = (long)(await extensionsCommand.ExecuteScalarAsync())!;

        extensions.ShouldBe(2L);
    }

    [Fact]
    public async Task Site_source_column_check_and_index_exist()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using (var column = new NpgsqlCommand(
            """
            select count(*) from information_schema.columns
            where table_name = 'sources' and column_name = 'category_id'
            """,
            connection))
        {
            var count = (long)(await column.ExecuteScalarAsync())!;
            count.ShouldBe(1L);
        }

        await using (var check = new NpgsqlCommand(
            """
            select count(*) from pg_constraint
            where conname = 'sources_site_category_check'
            """,
            connection))
        {
            var count = (long)(await check.ExecuteScalarAsync())!;
            count.ShouldBe(1L);
        }

        await using var index = new NpgsqlCommand(
            """
            select count(*) from pg_indexes
            where tablename = 'sources' and indexname = 'sources_site_category_uq'
            """,
            connection);

        var indexCount = (long)(await index.ExecuteScalarAsync())!;
        indexCount.ShouldBe(1L);
    }
}
