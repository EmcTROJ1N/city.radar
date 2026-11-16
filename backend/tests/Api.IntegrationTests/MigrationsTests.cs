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

}
