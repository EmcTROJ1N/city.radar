using System.Globalization;

using Api.IntegrationTests.Infrastructure;

using BuildingBlocks.Persistence;

using IdentityAccess.Contracts;
using IdentityAccess.Core;
using IdentityAccess.Core.Seeding;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

using Npgsql;

using Shouldly;

namespace Api.IntegrationTests.Identity;

/// <summary>
/// Seed ролей и dev-пользователей. БД у всей коллекции общая, поэтому
/// каждый тест начинается с очистки таблиц Identity, которые заполняют сидеры.
/// </summary>
[Collection("postgres")]
public sealed class SeedTests(PostgresFixture fixture)
{
    /// <summary>2 права у <c>user</c> + 10 у <c>moderator</c> + 14 у <c>admin</c>.</summary>
    private const int ExpectedPermissionClaimCount = 26;

    private const int ExpectedDevUserCount = 5;

    /// <summary>Dev-пользователь с ролью <c>user</c> (из <c>db/seed_dev.sql</c>).</summary>
    private static readonly Guid DevUserId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    [Fact]
    public async Task Moderator_role_has_no_secret_or_user_admin_permissions()
    {
        var cancellationToken = CancellationToken.None;
        await ResetIdentityAsync(cancellationToken);

        await using var provider = BuildProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<RoleSeeder>().SeedAsync(cancellationToken);
        }

        var permissions = await QueryRolePermissionsAsync(Roles.Moderator, cancellationToken);

        permissions.ShouldNotContain(Permissions.SourceSecretsManage);
        permissions.ShouldNotContain(Permissions.SettingsGlobalManage);
        permissions.ShouldNotContain(Permissions.UsersRolesAssign);
        permissions.ShouldNotContain(Permissions.AuditRead);

        permissions.ShouldBe(Roles.PermissionsFor(Roles.Moderator), ignoreOrder: true);
    }

    [Fact]
    public async Task Admin_has_all_permissions()
    {
        var cancellationToken = CancellationToken.None;
        await ResetIdentityAsync(cancellationToken);

        await using var provider = BuildProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<RoleSeeder>().SeedAsync(cancellationToken);
        }

        var permissions = await QueryRolePermissionsAsync(Roles.Admin, cancellationToken);

        permissions.Count.ShouldBe(14);
        permissions.ShouldBe(Permissions.All, ignoreOrder: true);
    }

    [Fact]
    public async Task Seed_is_idempotent()
    {
        var cancellationToken = CancellationToken.None;
        await ResetIdentityAsync(cancellationToken);

        await using var provider = BuildProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var roleSeeder = scope.ServiceProvider.GetRequiredService<RoleSeeder>();
            var devSeeder = scope.ServiceProvider.GetRequiredService<DevSeeder>();

            await roleSeeder.SeedAsync(cancellationToken);
            await devSeeder.SeedAsync(cancellationToken);
            await roleSeeder.SeedAsync(cancellationToken);
            await devSeeder.SeedAsync(cancellationToken);
        }

        (await CountAsync("roles", cancellationToken)).ShouldBe(3);
        (await CountAsync("role_claims", cancellationToken)).ShouldBe(ExpectedPermissionClaimCount);
        (await CountAsync("users", cancellationToken)).ShouldBe(ExpectedDevUserCount);
        (await CountAsync("user_roles", cancellationToken)).ShouldBe(ExpectedDevUserCount);
    }

    [Fact]
    public async Task Role_claims_are_reconciled()
    {
        var cancellationToken = CancellationToken.None;
        await ResetIdentityAsync(cancellationToken);

        await using var provider = BuildProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<RoleSeeder>().SeedAsync(cancellationToken);
        }

        // Портированная БД: лишнее право у user и потерянное эталонное право.
        await ExecuteAsync(
            """
            insert into role_claims (role_id, claim_value)
            select id, 'audit.read' from roles where name = 'user'
            """,
            cancellationToken);
        await ExecuteAsync(
            """
            delete from role_claims
            where role_id = (select id from roles where name = 'user')
              and claim_value = 'map.read'
            """,
            cancellationToken);

        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<RoleSeeder>().SeedAsync(cancellationToken);
        }

        var permissions = await QueryRolePermissionsAsync(Roles.User, cancellationToken);

        permissions.ShouldBe([Permissions.MapRead, Permissions.ObservationCreate], ignoreOrder: true);
        (await CountAsync("role_claims", cancellationToken)).ShouldBe(ExpectedPermissionClaimCount);
    }

    [Fact]
    public async Task Dev_seeder_creates_three_role_users()
    {
        var cancellationToken = CancellationToken.None;
        await ResetIdentityAsync(cancellationToken);

        await using var provider = BuildProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<RoleSeeder>().SeedAsync(cancellationToken);
            await scope.ServiceProvider.GetRequiredService<DevSeeder>().SeedAsync(cancellationToken);
        }

        var rolesByUser = await QueryUserRolesAsync(cancellationToken);

        rolesByUser[Guid.Parse("00000000-0000-0000-0000-0000000000a1")].ShouldBe(Roles.User);
        rolesByUser[Guid.Parse("00000000-0000-0000-0000-0000000000a2")].ShouldBe(Roles.User);
        rolesByUser[Guid.Parse("00000000-0000-0000-0000-0000000000a3")].ShouldBe(Roles.Moderator);
        rolesByUser[Guid.Parse("00000000-0000-0000-0000-0000000000a4")].ShouldBe(Roles.Moderator);
        rolesByUser[Guid.Parse("00000000-0000-0000-0000-0000000000a5")].ShouldBe(Roles.Admin);
    }

    [Fact]
    public async Task Production_environment_does_not_seed_dev_users()
    {
        var cancellationToken = CancellationToken.None;
        await ResetIdentityAsync(cancellationToken);

        await using var provider = BuildProvider();
        var runner = new IdentitySeedRunner(
            provider,
            new FakeHostEnvironment(Environments.Production));

        await runner.RunAsync(cancellationToken);

        (await CountAsync("roles", cancellationToken)).ShouldBe(3);
        (await CountAsync("role_claims", cancellationToken)).ShouldBe(ExpectedPermissionClaimCount);
        (await CountAsync("users", cancellationToken)).ShouldBe(0);
        (await CountAsync("user_roles", cancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Dev_seeder_does_not_restore_role_removed_through_admin_panel()
    {
        var cancellationToken = CancellationToken.None;
        await ResetIdentityAsync(cancellationToken);

        await using var provider = BuildProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<RoleSeeder>().SeedAsync(cancellationToken);
            await scope.ServiceProvider.GetRequiredService<DevSeeder>().SeedAsync(cancellationToken);
        }

        try
        {
            // Админ снял роль через интерфейс: привязки нет, в журнале — итоговый набор.
            await ExecuteAsync(
                $"""
                 delete from user_roles where user_id = '{DevUserId}';
                 insert into audit_entries (action, entity_type, entity_id, after)
                 values ('user.roles.assign', 'user', '{DevUserId}', jsonb_build_object('roles', '[]'::jsonb));
                 """,
                cancellationToken);

            await using (var scope = provider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<DevSeeder>().SeedAsync(cancellationToken);
            }

            (await CountUserRolesAsync(DevUserId, cancellationToken)).ShouldBe(0L);
        }
        finally
        {
            // Общая БД коллекции: убираем журнал и возвращаем dev-роли, как до теста.
            await ExecuteAsync("truncate table audit_entries;", cancellationToken);

            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DevSeeder>().SeedAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task Testing_environment_seeds_dev_users()
    {
        var cancellationToken = CancellationToken.None;
        await ResetIdentityAsync(cancellationToken);

        await using var provider = BuildProvider();
        var runner = new IdentitySeedRunner(
            provider,
            new FakeHostEnvironment("Testing"));

        await runner.RunAsync(cancellationToken);

        (await CountAsync("roles", cancellationToken)).ShouldBe(3);
        (await CountAsync("users", cancellationToken)).ShouldBe(ExpectedDevUserCount);
        (await CountAsync("user_roles", cancellationToken)).ShouldBe(ExpectedDevUserCount);
    }

    private ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddPersistence(fixture.ConnectionString)
            .AddIdentityAccess()
            .BuildServiceProvider();

    /// <summary>
    /// Чистит таблицы, которые заполняют сидеры. <c>audit_entries</c> не удаляем построчно:
    /// журнал append-only и DELETE триггером запрещён, но TRUNCATE строковые триггеры
    /// не вызывает. Обе таблицы очищаются перед <c>delete from users</c>: записи аудита и
    /// <c>global_settings.updated_by</c> ссылаются на пользователя, и FK не даст его удалить.
    /// <c>map_objects</c> чистится по той же причине: <c>created_by</c> ссылается на
    /// <c>users(id)</c> (миграция MapObjects). <c>sources</c> — по той же причине
    /// (<c>created_by</c>, миграция Sources); их политики и привязки удаляются каскадом.
    /// <c>source_statuses</c> удаляются до <c>sources</c> явно: FK на источник —
    /// <c>on delete cascade</c> (миграция SourceStatuses), но порядок не должен зависеть от него.
    /// <c>observations</c> чистятся <c>TRUNCATE ... CASCADE</c>, а не <c>DELETE</c>: триггер
    /// <c>observations_immutable</c> запрещает удаление строк, но на <c>TRUNCATE</c>
    /// строковые триггеры не срабатывают. Порядок — раньше <c>map_objects</c>, <c>sources</c>
    /// и <c>users</c>, на которые ссылаются наблюдения; <c>current_statuses</c> и
    /// <c>raw_messages</c> — до них по тем же причинам.
    /// </summary>
    private async Task ResetIdentityAsync(CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            """
            truncate table audit_entries;
            delete from global_settings;
            truncate table user_roles, role_claims, roles cascade;
            truncate table current_statuses cascade;
            truncate table observations cascade;
            truncate table moderation_cases;
            truncate table raw_messages cascade;
            delete from source_statuses;
            delete from map_objects;
            delete from sources;
            delete from secrets;
            truncate table outbox_messages;
            delete from users;
            """,
            cancellationToken);
    }

    private async Task<long> CountAsync(string table, CancellationToken cancellationToken)
    {
        var sql = table switch
        {
            "roles" => "select count(*) from roles",
            "role_claims" => "select count(*) from role_claims",
            "users" => "select count(*) from users",
            "user_roles" => "select count(*) from user_roles",
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Неизвестная таблица."),
        };

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);

        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private async Task<long> CountUserRolesAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "select count(*) from user_roles where user_id = @user_id",
            connection);
        command.Parameters.AddWithValue("user_id", userId);

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private async Task<List<string>> QueryRolePermissionsAsync(
        string roleName,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            select claim.claim_value
            from role_claims claim
            join roles role on role.id = claim.role_id
            where role.name = @role_name
              and claim.claim_type = 'permission'
            order by claim.claim_value
            """,
            connection);
        command.Parameters.AddWithValue("role_name", roleName);

        var permissions = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            permissions.Add(reader.GetString(0));
        }

        return permissions;
    }

    private async Task<Dictionary<Guid, string>> QueryUserRolesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            select link.user_id, role.name
            from user_roles link
            join roles role on role.id = link.role_id
            """,
            connection);

        var rolesByUser = new Dictionary<Guid, string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rolesByUser[reader.GetGuid(0)] = reader.GetString(1);
        }

        return rolesByUser;
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Подмена <see cref="IHostEnvironment"/>: runner проверяет только имя окружения.</summary>
    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Api.IntegrationTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
