using System.Diagnostics;

using Api.IntegrationTests.Infrastructure;

using Npgsql;

using Shouldly;

namespace Api.IntegrationTests.Deployment;

/// <summary>
/// Режим «только миграции»: <c>Api.Host --migrate-only</c> — отдельный шаг
/// деплоя, который доводит пустую БД до эталонной схемы и завершается с кодом 0. Повторный запуск
/// идемпотентен; dev-данные не сеются.
/// </summary>
/// <remarks>
/// Хост запускается отдельным процессом (как в <c>docker compose run migrate</c>), а не через
/// <c>WebApplicationFactory</c>: проверяется именно код выхода точки входа Api.Host.
/// </remarks>
[Collection("postgres")]
public sealed class MigrateOnlyTests(PostgresFixture fixture)
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task Migrate_only_creates_schema_and_is_idempotent()
    {
        var cancellationToken = CancellationToken.None;
        var databaseName = $"migrate_only_{Guid.NewGuid():N}";

        await CreateDatabaseAsync(fixture.ConnectionString, databaseName, cancellationToken);

        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = databaseName,
            Pooling = false,
        }.ConnectionString;

        try
        {
            var firstRun = await RunMigrateOnlyAsync(connectionString, cancellationToken);
            firstRun.ExitCode.ShouldBe(
                0,
                $"первый запуск --migrate-only должен завершиться успешно. Вывод:\n{firstRun.Output}");

            var tables = await ReadTableNamesAsync(connectionString, cancellationToken);
            tables.ShouldContain("__efmigrationshistory");
            tables.ShouldContain("users");
            tables.ShouldContain("roles");
            tables.ShouldContain("observations");
            tables.ShouldContain("current_statuses");
            tables.ShouldContain("source_statuses");

            // Роли и права — эталон из кода; dev-пользователей в проде (и в этом режиме) быть не должно.
            (await CountAsync(connectionString, "roles", cancellationToken)).ShouldBe(3L);
            (await CountAsync(connectionString, "users", cancellationToken)).ShouldBe(0L);
            (await CountAsync(connectionString, "role_claims", cancellationToken)).ShouldBeGreaterThan(0L);

            var secondRun = await RunMigrateOnlyAsync(connectionString, cancellationToken);
            secondRun.ExitCode.ShouldBe(
                0,
                $"повторный запуск --migrate-only должен быть идемпотентным. Вывод:\n{secondRun.Output}");

            (await CountAsync(connectionString, "roles", cancellationToken)).ShouldBe(3L);
            (await CountAsync(connectionString, "users", cancellationToken)).ShouldBe(0L);
        }
        finally
        {
            await DropDatabaseAsync(fixture.ConnectionString, databaseName, cancellationToken);
        }
    }

    [Fact]
    public async Task Missing_connection_string_returns_non_zero_exit_code()
    {
        var result = await RunMigrateOnlyAsync(
            connectionString: string.Empty,
            CancellationToken.None);

        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("ConnectionStrings:Mayak");
    }

    private static async Task CreateDatabaseAsync(
        string fixtureConnectionString,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(GetMaintenanceConnectionString(fixtureConnectionString));
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand($"create database \"{databaseName}\"", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DropDatabaseAsync(
        string fixtureConnectionString,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(GetMaintenanceConnectionString(fixtureConnectionString));
        await connection.OpenAsync(cancellationToken);

        // Тестовый процесс уже завершился, но пул основного процесса мог оставить соединение.
        NpgsqlConnection.ClearAllPools();

        await using var terminate = new NpgsqlCommand(
            """
            select pg_terminate_backend(pid)
            from pg_stat_activity
            where datname = @database and pid <> pg_backend_pid()
            """,
            connection);
        terminate.Parameters.AddWithValue("database", databaseName);
        await terminate.ExecuteNonQueryAsync(cancellationToken);

        await using var command = new NpgsqlCommand($"drop database if exists \"{databaseName}\"", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string GetMaintenanceConnectionString(string fixtureConnectionString) =>
        // Подключение к служебной БД контейнера: create/drop database нельзя выполнять из своей БД.
        new NpgsqlConnectionStringBuilder(fixtureConnectionString)
        {
            Database = "postgres",
            Pooling = false,
        }.ConnectionString;

    private static async Task<(int ExitCode, string Output)> RunMigrateOnlyAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        // Api.Host.dll и его runtimeconfig.json копируются в выход тестового проекта
        // (ProjectReference на исполняемый проект), поэтому запускаем именно собранный хост.
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, "Api.Host.dll");
        File.Exists(assemblyPath).ShouldBeTrue($"Не найден {assemblyPath}: хост должен быть собран.");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add("--migrate-only");

        // Пустое значение — проверка ветки «нет строки подключения».
        startInfo.Environment["ConnectionStrings__Mayak"] = connectionString;

        // Production-путь: миграции не должны требовать мастер-ключа секретов и JWT-ключа.
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
        startInfo.Environment.Remove("MAYAK_MASTER_KEY_FILE");
        startInfo.Environment.Remove("Jwt__SigningKey");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить процесс dotnet.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProcessTimeout);

        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"Процесс --migrate-only не завершился за {ProcessTimeout.TotalSeconds} с.");
        }

        return (process.ExitCode, $"{await stdout}\n{await stderr}");
    }

    private static async Task<HashSet<string>> ReadTableNamesAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "select table_name from information_schema.tables where table_schema = 'public'",
            connection);

        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static async Task<long> CountAsync(
        string connectionString,
        string table,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand($"select count(*) from \"{table}\"", connection);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
