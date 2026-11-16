using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Api.IntegrationTests.Identity;
using Api.IntegrationTests.Infrastructure;

using BuildingBlocks.Messaging;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Shouldly;

namespace Api.IntegrationTests.Catalog;

/// <summary>
/// Общий контекст тестов каталога: хост Api на общей БД коллекции, тестовые токены
/// dev-пользователей и запросы к <c>audit_entries</c>. Изоляция — уникальные коды и имена
/// на каждый тест (суффикс <see cref="NewSuffix"/>), глобальная очистка чужих таблиц не нужна.
/// </summary>
internal sealed class CatalogApi : IAsyncDisposable
{
    /// <summary>Dev-пользователь роли <c>user</c> (a1): нет прав управления каталогом.</summary>
    public static readonly Guid UserId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    /// <summary>Dev-модератор (a3): права управления каталогом есть (кроме админских).</summary>
    public static readonly Guid ModeratorId = Guid.Parse("00000000-0000-0000-0000-0000000000a3");

    private readonly PostgresFixture _fixture;

    public CatalogApi(
        PostgresFixture fixture,
        Action<IServiceCollection>? configureServices = null,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        _fixture = fixture;
        Factory = new TestApiFactory(fixture.ConnectionString, configureServices, settings);
        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });
    }

    public TestApiFactory Factory { get; }

    public HttpClient Client { get; }

    public async Task InitializeAsync()
    {
        // Первый запрос поднимает хост, применяет миграции и сеет роли/dev-пользователей.
        using var response = await Client.GetAsync("/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    public static string NewSuffix() => Guid.NewGuid().ToString("N")[..10];

    public Task<string> ModeratorTokenAsync(CancellationToken ct = default) =>
        TestAuth.CreateAccessTokenAsync(Factory.Services, ModeratorId, ct);

    public Task<string> UserTokenAsync(CancellationToken ct = default) =>
        TestAuth.CreateAccessTokenAsync(Factory.Services, UserId, ct);

    /// <summary>
    /// Диспетчер outbox destination <c>api</c>. В тестах фоновый цикл выключен
    /// (<c>Outbox:ApiDispatcherEnabled=false</c>), поэтому доставку запускает тест.
    /// </summary>
    public OutboxDispatcher GetApiDispatcher() =>
        Factory.Services.GetRequiredService<OutboxDispatcher>();

    public Task<HttpResponseMessage> GetAsync(
        string path,
        string? accessToken = null,
        CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, path, accessToken, json: null, ct);

    public Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? accessToken,
        string? json,
        CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(method, path);

        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return Client.SendAsync(request, ct);
    }

    public static async Task<JsonElement> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken ct = default)
    {
        var text = await response.Content.ReadAsStringAsync(ct);

        using var document = JsonDocument.Parse(text);

        return document.RootElement.Clone();
    }

    /// <summary>Создаёт категорию через API и возвращает её идентификатор.</summary>
    public async Task<Guid> CreateCategoryAsync(
        string accessToken,
        string? code = null,
        string? name = null,
        CancellationToken ct = default)
    {
        var suffix = NewSuffix();
        code ??= $"category_{suffix}";
        name ??= $"Категория {suffix}";

        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/v1/catalog/categories",
            accessToken,
            $$"""{"code":"{{code}}","name":"{{name}}"}""",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var json = await ReadJsonAsync(response, ct);

        return json.GetProperty("id").GetGuid();
    }

    /// <summary>Создаёт источник через API и возвращает его идентификатор.</summary>
    public async Task<Guid> CreateSourceAsync(
        string accessToken,
        string? name = null,
        CancellationToken ct = default)
    {
        var suffix = NewSuffix();
        name ??= $"Источник {suffix}";

        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/v1/sources",
            accessToken,
            $$"""
              {"name":"{{name}}","sourceType":"telegram_chat","chatId":"@src_{{suffix}}",
               "timeoutSeconds":3600,"userPrompt":""}
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
    }

    public async Task<AuditRow?> FindAuditAsync(
        string action,
        string entityId,
        CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select entry.actor_id, entry.entity_type, entry.entity_id, entry.before::text, entry.after::text
            from audit_entries entry
            where entry.action = @action and entry.entity_id = @entity_id
            order by entry.id desc
            limit 1
            """,
            connection);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("entity_id", entityId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new AuditRow(
            reader.IsDBNull(0) ? null : reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            ParseJson(reader.IsDBNull(3) ? null : reader.GetString(3)),
            ParseJson(reader.IsDBNull(4) ? null : reader.GetString(4)));
    }

    public async Task ExecuteAsync(string sql, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> CountAsync(string sql, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(sql, connection);

        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
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
}

/// <summary>Запись аудита, прочитанная напрямую из БД для проверки бизнес-изменений.</summary>
internal sealed record AuditRow(
    Guid? ActorId,
    string? EntityType,
    string? EntityId,
    JsonElement? Before,
    JsonElement? After);
