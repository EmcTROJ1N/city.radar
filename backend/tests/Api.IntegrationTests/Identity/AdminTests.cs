using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Api.IntegrationTests.Infrastructure;

using BuildingBlocks.Persistence;

using IdentityAccess.Contracts;
using IdentityAccess.Core.Seeding;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Shouldly;

namespace Api.IntegrationTests.Identity;

/// <summary>
/// Админка Identity &amp; Access: пользователи и роли, журнал аудита,
/// глобальные настройки. БД у коллекции общая, поэтому перед каждым тестом восстанавливаются
/// роли dev-пользователей (сидеры) и очищаются <c>global_settings</c>; <c>audit_entries</c>
/// append-only не чистится — записи фильтруются по уникальному действию/объекту.
/// </summary>
[Collection("postgres")]
public sealed class AdminTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly Guid User1Id = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Moderator1Id = Guid.Parse("00000000-0000-0000-0000-0000000000a3");
    private static readonly Guid Moderator2Id = Guid.Parse("00000000-0000-0000-0000-0000000000a4");
    private static readonly Guid AdminId = Guid.Parse("00000000-0000-0000-0000-0000000000a5");

    private const string AssignRolesAction = "user.roles.assign";
    private const string SettingsUpdateAction = "settings.update";

    private TestApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new TestApiFactory(fixture.ConnectionString);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });

        await ResetStateAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Admin_can_assign_moderator_and_it_is_audited()
    {
        var ct = CancellationToken.None;
        var token = await TestAuth.CreateAccessTokenAsync(_factory.Services, AdminId, ct);

        using (var response = await PutJsonAsync(
            $"/api/v1/admin/users/{User1Id}/roles",
            token,
            """{"roles":["user","moderator"]}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var user = json.RootElement;

            user.GetProperty("id").GetGuid().ShouldBe(User1Id);
            user.GetProperty("displayName").GetString().ShouldBe("Виктор");
            user.GetProperty("maxUserId").GetInt64().ShouldBe(900000001);
            ReadStringArray(user.GetProperty("roles")).ShouldBe(
                [Roles.User, Roles.Moderator],
                ignoreOrder: true);
        }

        var entry = await FindLatestAuditAsync(AssignRolesAction, "user", User1Id.ToString(), ct);
        entry.ShouldNotBeNull("Назначение роли должно попасть в журнал аудита.");

        entry!.ActorId.ShouldBe(AdminId);
        entry.ActorName.ShouldBe("Герман");
        entry.Before.ShouldNotBeNull();
        entry.After.ShouldNotBeNull();
        ReadStringArray(entry.Before!.Value.GetProperty("roles")).ShouldBe([Roles.User]);
        ReadStringArray(entry.After!.Value.GetProperty("roles")).ShouldBe(
            [Roles.User, Roles.Moderator],
            ignoreOrder: true);

        // Через API журнал отдаёт before/after объектами (схема AuditEntry), а не строками jsonb.
        using (var response = await _client.SendAsync(
            CreateRequest(
                HttpMethod.Get,
                $"/api/v1/admin/audit?action={AssignRolesAction}&entityId={User1Id}",
                token),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var item = json.RootElement.GetProperty("items")[0];

            item.GetProperty("entityType").GetString().ShouldBe("user");
            ReadStringArray(item.GetProperty("before").GetProperty("roles")).ShouldBe([Roles.User]);
            ReadStringArray(item.GetProperty("after").GetProperty("roles")).ShouldBe(
                [Roles.User, Roles.Moderator],
                ignoreOrder: true);
        }

        // Восстанавливаем исходный набор ролей пользователя a1.
        using var restore = await PutJsonAsync(
            $"/api/v1/admin/users/{User1Id}/roles",
            token,
            """{"roles":["user"]}""",
            ct);
        restore.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await CountUserRolesAsync(User1Id, ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Moderator_cannot_read_audit_403()
    {
        var ct = CancellationToken.None;
        var token = await TestAuth.CreateAccessTokenAsync(_factory.Services, Moderator1Id, ct);

        using var response = await _client.SendAsync(
            CreateRequest(HttpMethod.Get, "/api/v1/admin/audit", token),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Anonymous_audit_401()
    {
        var ct = CancellationToken.None;

        using var response = await _client.SendAsync(
            CreateRequest(HttpMethod.Get, "/api/v1/admin/audit", accessToken: null),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Assign_unknown_role_422()
    {
        var ct = CancellationToken.None;
        var token = await TestAuth.CreateAccessTokenAsync(_factory.Services, AdminId, ct);

        using var response = await PutJsonAsync(
            $"/api/v1/admin/users/{User1Id}/roles",
            token,
            """{"roles":["user","superuser"]}""",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var error = json.RootElement.GetProperty("errors")[0];

        error.GetProperty("field").GetString().ShouldStartWith("roles");
        error.GetProperty("message").GetString()!.ShouldContain("superuser");

        // Неизвестная роль не меняет набор ролей пользователя.
        (await CountUserRolesAsync(User1Id, ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Assign_roles_to_unknown_user_404()
    {
        var ct = CancellationToken.None;
        var token = await TestAuth.CreateAccessTokenAsync(_factory.Services, AdminId, ct);
        var unknownUserId = Guid.NewGuid();

        using var response = await PutJsonAsync(
            $"/api/v1/admin/users/{unknownUserId}/roles",
            token,
            """{"roles":["user"]}""",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        json.RootElement.GetProperty("title").GetString().ShouldBe("Не найдено");
        json.RootElement.GetProperty("status").GetInt32().ShouldBe(404);
    }

    [Fact]
    public async Task Settings_unknown_key_422()
    {
        var ct = CancellationToken.None;
        var token = await TestAuth.CreateAccessTokenAsync(_factory.Services, AdminId, ct);

        using var response = await PutJsonAsync(
            "/api/v1/admin/settings",
            token,
            """{"unknownSetting":1}""",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        json.RootElement.GetProperty("errors")[0].GetProperty("field").GetString()
            .ShouldBe("unknownSetting");

        (await CountRowsAsync("global_settings", ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Settings_invalid_value_422()
    {
        var ct = CancellationToken.None;
        var token = await TestAuth.CreateAccessTokenAsync(_factory.Services, AdminId, ct);

        using (var response = await PutJsonAsync(
            "/api/v1/admin/settings",
            token,
            """{"commentMaxLength":0}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var error = json.RootElement.GetProperty("errors")[0];

            error.GetProperty("field").GetString().ShouldBe("commentMaxLength");
            error.GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace();
        }

        // freshnessFreshRatio — доля таймаута, границы 0 и 1 исключены.
        using (var response = await PutJsonAsync(
            "/api/v1/admin/settings",
            token,
            """{"freshnessFreshRatio":1}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        (await CountRowsAsync("global_settings", ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Settings_update_is_audited()
    {
        var ct = CancellationToken.None;
        var token = await TestAuth.CreateAccessTokenAsync(_factory.Services, AdminId, ct);

        using (var response = await PutJsonAsync(
            "/api/v1/admin/settings",
            token,
            """{"commentMaxLength":750}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var settings = json.RootElement;

            settings.GetProperty("commentMaxLength").GetInt32().ShouldBe(750);
            settings.GetProperty("observationRatePerHour").GetInt32().ShouldBe(20);
            settings.GetProperty("proxySecretId").ValueKind.ShouldBe(JsonValueKind.Null);
        }

        var row = await ReadSettingRowAsync("comment_max_length", ct);
        row.Value.ShouldBe("750");
        row.UpdatedBy.ShouldBe(AdminId);

        var entry = await FindLatestAuditAsync(SettingsUpdateAction, "settings", entityId: null, ct);
        entry.ShouldNotBeNull("Изменение настроек должно попасть в журнал аудита.");
        entry!.ActorId.ShouldBe(AdminId);

        entry.Before.ShouldNotBeNull();
        entry.Before!.Value.EnumerateObject().Select(property => property.Name)
            .ShouldBe(["commentMaxLength"]);
        entry.Before!.Value.GetProperty("commentMaxLength").GetInt32().ShouldBe(500);

        entry.After.ShouldNotBeNull();
        entry.After!.Value.EnumerateObject().Select(property => property.Name)
            .ShouldBe(["commentMaxLength"]);
        entry.After!.Value.GetProperty("commentMaxLength").GetInt32().ShouldBe(750);
    }

    [Fact]
    public async Task Settings_get_returns_defaults()
    {
        var ct = CancellationToken.None;
        var token = await TestAuth.CreateAccessTokenAsync(_factory.Services, AdminId, ct);

        using var response = await _client.SendAsync(
            CreateRequest(HttpMethod.Get, "/api/v1/admin/settings", token),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var settings = json.RootElement;

        settings.GetProperty("observationRatePerHour").GetInt32().ShouldBe(20);
        settings.GetProperty("commentMaxLength").GetInt32().ShouldBe(500);
        settings.GetProperty("confidenceThresholdDefault").GetDouble().ShouldBe(0.7);
        settings.GetProperty("anomalyWindowMinutes").GetInt32().ShouldBe(5);
        settings.GetProperty("anomalyThreshold").GetInt32().ShouldBe(30);
        settings.GetProperty("freshnessFreshRatio").GetDouble().ShouldBe(0.5);
        settings.GetProperty("proxySecretId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Moderator_cannot_access_settings_403()
    {
        var ct = CancellationToken.None;
        var token = await TestAuth.CreateAccessTokenAsync(_factory.Services, Moderator1Id, ct);

        using (var response = await _client.SendAsync(
            CreateRequest(HttpMethod.Get, "/api/v1/admin/settings", token),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var response = await PutJsonAsync(
            "/api/v1/admin/settings",
            token,
            """{"commentMaxLength":600}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        }

        (await CountRowsAsync("global_settings", ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Users_list_filters_by_role_and_query()
    {
        var ct = CancellationToken.None;
        var token = await TestAuth.CreateAccessTokenAsync(_factory.Services, AdminId, ct);

        // role=moderator — только модераторы a3 и a4.
        using (var response = await _client.SendAsync(
            CreateRequest(HttpMethod.Get, "/api/v1/admin/users?role=moderator", token),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            ReadUserIds(json.RootElement).ShouldBe([Moderator1Id, Moderator2Id]);
        }

        // q ищет по имени (регистронезависимо).
        var escapedQuery = Uri.EscapeDataString("Ольга");
        using (var response = await _client.SendAsync(
            CreateRequest(HttpMethod.Get, $"/api/v1/admin/users?q={escapedQuery}", token),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            ReadUserIds(json.RootElement).ShouldBe([Moderator1Id]);
        }

        // Курсорная пагинация: limit + nextCursor без потерь и дублей.
        string? cursor;
        using (var response = await _client.SendAsync(
            CreateRequest(HttpMethod.Get, "/api/v1/admin/users?limit=2", token),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            ReadUserIds(json.RootElement).ShouldBe([User1Id, Guid.Parse("00000000-0000-0000-0000-0000000000a2")]);
            cursor = json.RootElement.GetProperty("nextCursor").GetString();
            cursor.ShouldNotBeNull();
        }

        using (var response = await _client.SendAsync(
            CreateRequest(HttpMethod.Get, $"/api/v1/admin/users?limit=2&cursor={cursor}", token),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            ReadUserIds(json.RootElement).ShouldBe([Moderator1Id, Moderator2Id]);
            cursor = json.RootElement.GetProperty("nextCursor").GetString();
            cursor.ShouldNotBeNull();
        }

        using (var response = await _client.SendAsync(
            CreateRequest(HttpMethod.Get, $"/api/v1/admin/users?limit=2&cursor={cursor}", token),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            ReadUserIds(json.RootElement).ShouldBe([AdminId]);
            json.RootElement.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task Audit_filters_by_action_and_paginates()
    {
        var ct = CancellationToken.None;
        var token = await TestAuth.CreateAccessTokenAsync(_factory.Services, AdminId, ct);

        // Уникальное действие изолирует тест от записей других тестов.
        var action = $"test.audit.{Guid.NewGuid():N}";
        await InsertAuditEntriesAsync(action, ct);

        var escapedAction = Uri.EscapeDataString(action);
        string? cursor;

        using (var response = await _client.SendAsync(
            CreateRequest(HttpMethod.Get, $"/api/v1/admin/audit?action={escapedAction}&limit=2", token),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var page = json.RootElement;
            var items = page.GetProperty("items").EnumerateArray().ToArray();

            items.Length.ShouldBe(2);
            items[0].GetProperty("reason").GetString().ShouldBe("третья");
            items[1].GetProperty("reason").GetString().ShouldBe("вторая");
            items[0].GetProperty("actorId").GetGuid().ShouldBe(AdminId);
            items[0].GetProperty("actorName").GetString().ShouldBe("Герман");
            items[0].GetProperty("action").GetString().ShouldBe(action);

            cursor = page.GetProperty("nextCursor").GetString();
            cursor.ShouldNotBeNull();
        }

        using (var response = await _client.SendAsync(
            CreateRequest(
                HttpMethod.Get,
                $"/api/v1/admin/audit?action={escapedAction}&limit=2&cursor={cursor}",
                token),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var page = json.RootElement;
            var items = page.GetProperty("items").EnumerateArray().ToArray();

            items.Length.ShouldBe(1);
            items[0].GetProperty("reason").GetString().ShouldBe("первая");
            page.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        }

        // Фильтр по актору работает вместе с фильтром по действию.
        using (var response = await _client.SendAsync(
            CreateRequest(
                HttpMethod.Get,
                $"/api/v1/admin/audit?action={escapedAction}&actorId={AdminId}",
                token),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            json.RootElement.GetProperty("items").GetArrayLength().ShouldBe(3);
        }
    }

    [Fact]
    public async Task Audit_is_written_in_same_transaction()
    {
        var ct = CancellationToken.None;
        var action = $"test.transaction.{Guid.NewGuid():N}";

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MayakDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLog>();

        // WriteAsync не сохраняет сам: вне SaveChanges записи в БД нет.
        await audit.WriteAsync(action, "test", action, null, new { value = 1 }, ct: ct);
        (await CountAuditAsync(action, ct)).ShouldBe(0);

        // Запись уходит в БД вместе с SaveChanges вызывающего кода.
        await db.SaveChangesAsync(ct);
        (await CountAuditAsync(action, ct)).ShouldBe(1);

        // Падение бизнес-изменения откатывает транзакцию вместе с записью аудита.
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await audit.WriteAsync(action, "test", action, null, new { value = 2 }, ct: ct);
            await db.SaveChangesAsync(ct);

            await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
                $"insert into global_settings (key, value) values ({action}, null)",
                ct));

            await transaction.RollbackAsync(ct);
        }

        (await CountAuditAsync(action, ct)).ShouldBe(1);
    }

    /// <summary>
    /// Изоляция состояния: роли dev-пользователей восстанавливаются сидерами, настройки очищаются.
    /// </summary>
    private async Task ResetStateAsync(CancellationToken ct)
    {
        await ExecuteAsync(
            """
            delete from user_roles;
            delete from global_settings;
            """,
            ct);

        await using var scope = _factory.Services.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<RoleSeeder>().SeedAsync(ct);
        await scope.ServiceProvider.GetRequiredService<DevSeeder>().SeedAsync(ct);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string? accessToken)
    {
        var request = new HttpRequestMessage(method, path);

        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return request;
    }

    private async Task<HttpResponseMessage> PutJsonAsync(
        string path,
        string? accessToken,
        string json,
        CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Put, path, accessToken);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        return await _client.SendAsync(request, ct);
    }

    private static Guid[] ReadUserIds(JsonElement page) =>
        page.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .ToArray();

    private static string[] ReadStringArray(JsonElement element) =>
        element.EnumerateArray().Select(item => item.GetString()!).ToArray();

    private async Task<AuditRow?> FindLatestAuditAsync(
        string action,
        string? entityType,
        string? entityId,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select entry.actor_id, actor.display_name, entry.entity_type, entry.entity_id,
                   entry.before::text, entry.after::text
            from audit_entries entry
            left join users actor on actor.id = entry.actor_id
            where entry.action = @action
              and (@entity_type::text is null or entry.entity_type = @entity_type)
              and (@entity_id::text is null or entry.entity_id = @entity_id)
            order by entry.id desc
            limit 1
            """,
            connection);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("entity_type", (object?)entityType ?? DBNull.Value);
        command.Parameters.AddWithValue("entity_id", (object?)entityId ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new AuditRow(
            reader.IsDBNull(0) ? null : reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            ParseJson(reader.IsDBNull(4) ? null : reader.GetString(4)),
            ParseJson(reader.IsDBNull(5) ? null : reader.GetString(5)));
    }

    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }

    private async Task<(string Value, Guid? UpdatedBy)> ReadSettingRowAsync(
        string key,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            "select value::text, updated_by from global_settings where key = @key",
            connection);
        command.Parameters.AddWithValue("key", key);

        await using var reader = await command.ExecuteReaderAsync(ct);
        (await reader.ReadAsync(ct)).ShouldBeTrue($"В global_settings нет ключа {key}.");

        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetGuid(1));
    }

    private async Task InsertAuditEntriesAsync(string action, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            insert into audit_entries (actor_id, action, reason, at)
            values (@actor, @action, 'первая', now() - interval '3 minutes'),
                   (@actor, @action, 'вторая', now() - interval '2 minutes'),
                   (@actor, @action, 'третья', now() - interval '1 minute')
            """,
            connection);
        command.Parameters.AddWithValue("actor", AdminId);
        command.Parameters.AddWithValue("action", action);

        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<long> CountAuditAsync(string action, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            "select count(*) from audit_entries where action = @action",
            connection);
        command.Parameters.AddWithValue("action", action);

        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private async Task<long> CountUserRolesAsync(Guid userId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            "select count(*) from user_roles where user_id = @user_id",
            connection);
        command.Parameters.AddWithValue("user_id", userId);

        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private async Task<long> CountRowsAsync(string table, CancellationToken ct)
    {
        var sql = table switch
        {
            "global_settings" => "select count(*) from global_settings",
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Неизвестная таблица."),
        };

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(sql, connection);

        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed record AuditRow(
        Guid? ActorId,
        string? ActorName,
        string? EntityType,
        string? EntityId,
        JsonElement? Before,
        JsonElement? After);
}
