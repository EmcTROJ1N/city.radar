using System.Net;
using System.Text;
using System.Text.Json;

using Api.IntegrationTests.Catalog;
using Api.IntegrationTests.Identity;
using Api.IntegrationTests.Infrastructure;

using BuildingBlocks.Persistence;
using BuildingBlocks.Security;

using IdentityAccess.Core;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Shouldly;

using Sources.Contracts;
using Sources.Core;

namespace Api.IntegrationTests.Sources;

/// <summary>
/// Секреты и подключение источников
///). Проверяется, что значения и шифртекст никогда не покидают сервер, модератор
/// не имеет доступа, второй секрет LLM заменяет первый, аудит не содержит значений, сессия
/// назначается только Telegram-источнику, а прокси разрешается в порядке
/// «источник → аккаунт → глобальный». Изоляция — уникальные названия и идентификаторы.
/// </summary>
[Collection("postgres")]
public sealed class SecretsTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string SecretsPath = "/api/v1/admin/secrets";
    private const string SourcesPath = "/api/v1/sources";
    private const string ConnectionsPath = "/api/v1/admin/sources";
    private const string SettingsPath = "/api/v1/admin/settings";

    private static readonly Guid AdminId = Guid.Parse("00000000-0000-0000-0000-0000000000a5");

    private CatalogApi _api = null!;

    public async Task InitializeAsync()
    {
        _api = new CatalogApi(fixture);
        await _api.InitializeAsync();
    }

    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task Admin_can_replace_but_not_read_secret()
    {
        var ct = CancellationToken.None;
        var token = await AdminTokenAsync(ct);
        var secretId = Guid.NewGuid();
        var name = $"Сессия {CatalogApi.NewSuffix()}";
        var firstValue = $"SESSION-FIRST-{CatalogApi.NewSuffix()}";
        var secondValue = $"SESSION-SECOND-{CatalogApi.NewSuffix()}";

        using (var response = await PutTelegramSessionAsync(
            token,
            secretId,
            name,
            firstValue,
            proxyId: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            (await response.Content.ReadAsStringAsync(ct)).ShouldBeEmpty();
        }

        using (var response = await PutTelegramSessionAsync(
            token,
            secretId,
            name,
            secondValue,
            proxyId: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            (await response.Content.ReadAsStringAsync(ct)).ShouldBeEmpty();
        }

        using (var response = await _api.GetAsync(SecretsPath, token, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var body = await response.Content.ReadAsStringAsync(ct);
            body.ShouldNotContain(firstValue);
            body.ShouldNotContain(secondValue);
            body.ShouldNotContain("ciphertext", Case.Insensitive);
            body.ShouldNotContain("keyVersion", Case.Insensitive);
        }

        var (keyVersion, ciphertext) = await ReadCiphertextAsync(secretId, ct);
        var cipher = _api.Factory.Services.GetRequiredService<ISecretCipher>();

        keyVersion.ShouldBe(cipher.CurrentKeyVersion);
        ciphertext.ShouldNotBe(Encoding.UTF8.GetBytes(secondValue));
        Encoding.UTF8.GetString(cipher.Decrypt(ciphertext, keyVersion)).ShouldBe(secondValue);
    }

    [Fact]
    public async Task Telegram_mtproto_session_is_listed_with_userbot_mode()
    {
        var ct = CancellationToken.None;
        var token = await AdminTokenAsync(ct);
        var secretId = Guid.NewGuid();
        var session = JsonSerializer.Serialize(new
        {
            apiId = 1234567,
            apiHash = "0123456789abcdef0123456789abcdef",
            sessionData = $"SESSION-DATA-{CatalogApi.NewSuffix()}",
            phone = "+79990000000",
        });

        using (var response = await PutTelegramSessionAsync(
            token,
            secretId,
            $"MTProto {CatalogApi.NewSuffix()}",
            session,
            proxyId: null,
            ct,
            mode: "userbot"))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var list = await _api.GetAsync(SecretsPath, token, ct);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);

        var item = (await CatalogApi.ReadJsonAsync(list, ct)).EnumerateArray()
            .Single(secret => secret.GetProperty("id").GetGuid() == secretId);
        item.GetProperty("kind").GetString().ShouldBe("telegram_session");
        item.GetProperty("mode").GetString().ShouldBe("userbot");
    }

    [Fact]
    public async Task Moderator_gets_403_on_all_secret_endpoints()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);

        using (var response = await _api.GetAsync(SecretsPath, token, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var response = await PutTelegramSessionAsync(
            token,
            Guid.NewGuid(),
            $"Сессия {CatalogApi.NewSuffix()}",
            $"SESSION-{CatalogApi.NewSuffix()}",
            proxyId: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var response = await PutProxyAsync(
            token,
            Guid.NewGuid(),
            $"Прокси {CatalogApi.NewSuffix()}",
            "proxy.example",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var response = await PutLlmAsync(
            token,
            $"sk-{CatalogApi.NewSuffix()}",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task Moderator_connection_endpoint_403()
    {
        var ct = CancellationToken.None;
        var token = await _api.ModeratorTokenAsync(ct);
        var sourceId = await _api.CreateSourceAsync(token, ct: ct);

        using (var response = await _api.GetAsync(
            $"{ConnectionsPath}/{sourceId}/connection",
            token,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"{ConnectionsPath}/{sourceId}/connection",
            token,
            ConnectionJson(null, null),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task Anonymous_401()
    {
        var ct = CancellationToken.None;

        using (var response = await _api.GetAsync(SecretsPath, accessToken: null, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var response = await PutLlmAsync(null, $"sk-{CatalogApi.NewSuffix()}", ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var response = await _api.GetAsync(
            $"{ConnectionsPath}/{Guid.NewGuid()}/connection",
            accessToken: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"{ConnectionsPath}/{Guid.NewGuid()}/connection",
            accessToken: null,
            ConnectionJson(null, null),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
    }

    [Fact]
    public async Task List_shows_hasValue_without_value()
    {
        var ct = CancellationToken.None;
        var token = await AdminTokenAsync(ct);
        var name = $"Прокси {CatalogApi.NewSuffix()}";
        var password = $"PROXY-PASSWORD-{CatalogApi.NewSuffix()}";
        var proxyId = Guid.NewGuid();

        using (var response = await PutProxyAsync(
            token,
            proxyId,
            name,
            "proxy.example",
            ct,
            password: password))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var listResponse = await _api.GetAsync(SecretsPath, token, ct);
        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await listResponse.Content.ReadAsStringAsync(ct);
        body.ShouldNotContain(password);
        body.ShouldNotContain("ciphertext", Case.Insensitive);

        var json = await CatalogApi.ReadJsonAsync(listResponse, ct);
        var item = json.EnumerateArray()
            .Single(element => element.GetProperty("id").GetGuid() == proxyId);

        item.GetProperty("kind").GetString().ShouldBe("proxy");
        item.GetProperty("name").GetString().ShouldBe(name);
        item.GetProperty("hasValue").GetBoolean().ShouldBeTrue();
        item.GetProperty("updatedAt").ValueKind.ShouldBe(JsonValueKind.String);
        item.TryGetProperty("value", out _).ShouldBeFalse();
        item.TryGetProperty("ciphertext", out _).ShouldBeFalse();
        item.TryGetProperty("password", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Second_llm_secret_replaces_not_duplicates()
    {
        var ct = CancellationToken.None;
        var token = await AdminTokenAsync(ct);
        var firstKey = $"sk-first-{CatalogApi.NewSuffix()}";
        var secondKey = $"sk-second-{CatalogApi.NewSuffix()}";

        using (var response = await PutLlmAsync(token, firstKey, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using (var response = await PutLlmAsync(token, secondKey, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await CountAsync("select count(*) from secrets where kind = 'llm'", ct)).ShouldBe(1L);

        var (llmId, keyVersion, ciphertext) = await ReadLlmSecretAsync(ct);
        var cipher = _api.Factory.Services.GetRequiredService<ISecretCipher>();
        var payload = Encoding.UTF8.GetString(cipher.Decrypt(ciphertext, keyVersion));

        payload.ShouldContain(secondKey);
        payload.ShouldNotContain(firstKey);

        using var listResponse = await _api.GetAsync(SecretsPath, token, ct);
        var json = await CatalogApi.ReadJsonAsync(listResponse, ct);
        var llmItems = json.EnumerateArray()
            .Where(element => element.GetProperty("kind").GetString() == "llm")
            .ToArray();

        llmItems.Length.ShouldBe(1);
        llmItems[0].GetProperty("id").GetGuid().ShouldBe(llmId);
    }

    [Fact]
    public void Api_host_has_no_ISecretReader_registered()
    {
        _api.Factory.Services.GetService<ISecretReader>().ShouldBeNull(
            "Reader секретов регистрируется только в composition root Worker-а.");
        _api.Factory.Services.GetService<ISourceConnectionResolver>().ShouldBeNull();
    }

    [Fact]
    public async Task Proxy_change_is_audited_without_value()
    {
        var ct = CancellationToken.None;
        var token = await AdminTokenAsync(ct);
        var proxyId = Guid.NewGuid();
        var name = $"Прокси {CatalogApi.NewSuffix()}";
        var username = $"PROXY-USER-{CatalogApi.NewSuffix()}";
        var firstPassword = $"PROXY-PASSWORD-ONE-{CatalogApi.NewSuffix()}";
        var secondPassword = $"PROXY-PASSWORD-TWO-{CatalogApi.NewSuffix()}";

        using (var response = await PutProxyAsync(
            token,
            proxyId,
            name,
            "proxy.example",
            ct,
            username,
            firstPassword))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using (var response = await PutProxyAsync(
            token,
            proxyId,
            name,
            "proxy2.example",
            ct,
            username,
            secondPassword))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var audit = await ReadLatestAuditAsync(proxyId, ct);
        audit.ShouldNotBeNull("Изменение прокси должно попасть в аудит.");
        var latest = audit!;
        latest.EntityType.ShouldBe("secret");
        latest.Action.ShouldBe("secret.replace");
        latest.After.ShouldNotBeNull();
        latest.After!.ShouldContain("proxy");
        latest.After.ShouldContain("keyVersion");
        latest.After.ShouldContain("replaced");

        foreach (var text in new[] { latest.Before, latest.After })
        {
            text.ShouldNotBeNull();
            text.ShouldNotContain(firstPassword);
            text.ShouldNotContain(secondPassword);
            text.ShouldNotContain(username);
        }
    }

    [Fact]
    public async Task Session_assignable_to_telegram_source_only()
    {
        var ct = CancellationToken.None;
        var token = await AdminTokenAsync(ct);
        var telegramSourceId = await _api.CreateSourceAsync(token, ct: ct);
        var maxSourceId = await CreateMaxSourceAsync(token, ct);
        var sessionId = Guid.NewGuid();

        using (var response = await PutTelegramSessionAsync(
            token,
            sessionId,
            $"Сессия {CatalogApi.NewSuffix()}",
            $"SESSION-{CatalogApi.NewSuffix()}",
            proxyId: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"{ConnectionsPath}/{telegramSourceId}/connection",
            token,
            ConnectionJson(sessionId, null),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var json = await CatalogApi.ReadJsonAsync(response, ct);
            json.GetProperty("telegramSessionId").GetGuid().ShouldBe(sessionId);
            json.GetProperty("proxyId").ValueKind.ShouldBe(JsonValueKind.Null);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"{ConnectionsPath}/{maxSourceId}/connection",
            token,
            ConnectionJson(sessionId, null),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

            var json = await CatalogApi.ReadJsonAsync(response, ct);
            json.GetProperty("errors").EnumerateArray()
                .ShouldContain(error => error.GetProperty("field").GetString() == "telegramSessionId");
        }

        (await CountAsync(
            $"select count(*) from sources where id = '{maxSourceId}' and telegram_session_id is null",
            ct)).ShouldBe(1L);
    }

    [Fact]
    public async Task Wrong_secret_kind_in_connection_422()
    {
        var ct = CancellationToken.None;
        var token = await AdminTokenAsync(ct);
        var sourceId = await _api.CreateSourceAsync(token, ct: ct);

        var sessionId = Guid.NewGuid();
        using (var response = await PutTelegramSessionAsync(
            token,
            sessionId,
            $"Сессия {CatalogApi.NewSuffix()}",
            $"SESSION-{CatalogApi.NewSuffix()}",
            proxyId: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var proxyId = Guid.NewGuid();
        using (var response = await PutProxyAsync(
            token,
            proxyId,
            $"Прокси {CatalogApi.NewSuffix()}",
            "proxy.example",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // Прокси в поле сессии.
        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"{ConnectionsPath}/{sourceId}/connection",
            token,
            ConnectionJson(proxyId, null),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("errors").EnumerateArray()
                .ShouldContain(error => error.GetProperty("field").GetString() == "telegramSessionId");
        }

        // Сессия в поле прокси.
        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"{ConnectionsPath}/{sourceId}/connection",
            token,
            ConnectionJson(null, sessionId),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("errors").EnumerateArray()
                .ShouldContain(error => error.GetProperty("field").GetString() == "proxyId");
        }

        using (var response = await _api.GetAsync(
            $"{ConnectionsPath}/{sourceId}/connection",
            token,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var json = await CatalogApi.ReadJsonAsync(response, ct);
            json.GetProperty("telegramSessionId").ValueKind.ShouldBe(JsonValueKind.Null);
            json.GetProperty("proxyId").ValueKind.ShouldBe(JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task Resolver_order_source_then_account_then_global()
    {
        var ct = CancellationToken.None;
        var token = await AdminTokenAsync(ct);
        var sourceId = await _api.CreateSourceAsync(token, ct: ct);

        var accountHost = $"account-{CatalogApi.NewSuffix()}.example";
        var sourceHost = $"source-{CatalogApi.NewSuffix()}.example";
        var globalHost = $"global-{CatalogApi.NewSuffix()}.example";

        var accountProxyId = await CreateProxyAsync(token, accountHost, ct);
        var sourceProxyId = await CreateProxyAsync(token, sourceHost, ct);
        var globalProxyId = await CreateProxyAsync(token, globalHost, ct);

        var sessionValue = $"SESSION-{CatalogApi.NewSuffix()}";
        var sessionId = Guid.NewGuid();
        using (var response = await PutTelegramSessionAsync(
            token,
            sessionId,
            $"Сессия {CatalogApi.NewSuffix()}",
            sessionValue,
            accountProxyId,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"{ConnectionsPath}/{sourceId}/connection",
            token,
            ConnectionJson(sessionId, null),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            SettingsPath,
            token,
            JsonSerializer.Serialize(new { proxySecretId = globalProxyId }),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await using var provider = BuildWorkerProvider();

        async Task<ResolvedConnection> ResolveAsync()
        {
            await using var scope = provider.CreateAsyncScope();
            var resolver = scope.ServiceProvider.GetRequiredService<ISourceConnectionResolver>();
            var connection = await resolver.ResolveAsync(sourceId, ct);

            connection.ShouldNotBeNull();

            return connection!;
        }

        // Прокси аккаунта Telegram важнее глобального.
        var resolved = await ResolveAsync();
        resolved.SessionSecret.ShouldBe(sessionValue);
        resolved.Proxy.ShouldNotBeNull();
        resolved.Proxy!.Host.ShouldBe(accountHost);
        resolved.ProxyRequired.ShouldBeTrue();

        // Прокси источника важнее прокси аккаунта.
        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"{ConnectionsPath}/{sourceId}/connection",
            token,
            ConnectionJson(sessionId, sourceProxyId),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        resolved = await ResolveAsync();
        resolved.Proxy!.Host.ShouldBe(sourceHost);

        // Без прокси источника снова действует прокси аккаунта.
        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"{ConnectionsPath}/{sourceId}/connection",
            token,
            ConnectionJson(sessionId, null),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        resolved = await ResolveAsync();
        resolved.Proxy!.Host.ShouldBe(accountHost);

        // Без прокси аккаунта действует глобальный.
        using (var response = await PutTelegramSessionAsync(
            token,
            sessionId,
            $"Сессия {CatalogApi.NewSuffix()}",
            sessionValue,
            proxyId: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        resolved = await ResolveAsync();
        resolved.Proxy!.Host.ShouldBe(globalHost);
        resolved.ProxyRequired.ShouldBeTrue();

        // Прокси не задан ни в одном звене — прямое соединение разрешено.
        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            SettingsPath,
            token,
            JsonSerializer.Serialize(new { proxySecretId = (Guid?)null }),
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        resolved = await ResolveAsync();
        resolved.Proxy.ShouldBeNull();
        resolved.ProxyRequired.ShouldBeFalse();
    }

    [Theory]
    [InlineData(
        """{"name":"Прокси","scheme":"ftp","host":"proxy.example","port":1080}""",
        "scheme")]
    [InlineData(
        """{"name":"Прокси","scheme":"socks5","host":"proxy.example","port":0}""",
        "port")]
    [InlineData(
        """{"name":"Прокси","scheme":"socks5","host":"proxy.example","port":70000}""",
        "port")]
    [InlineData(
        """{"name":"Прокси","scheme":"socks5","host":"","port":1080}""",
        "host")]
    public async Task Invalid_proxy_422(string json, string field)
    {
        var ct = CancellationToken.None;
        var token = await AdminTokenAsync(ct);

        using var response = await _api.SendAsync(
            HttpMethod.Put,
            $"{SecretsPath}/proxies/{Guid.NewGuid()}",
            token,
            json,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var body = await CatalogApi.ReadJsonAsync(response, ct);
        body.GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == field);
    }

    [Theory]
    [InlineData("""{"apiKey":"sk-test","baseUrl":"ftp://api.example","model":"gpt"}""", "baseUrl")]
    [InlineData("""{"apiKey":"sk-test","baseUrl":"не url","model":"gpt"}""", "baseUrl")]
    [InlineData("""{"apiKey":"","baseUrl":"https://api.example","model":"gpt"}""", "apiKey")]
    [InlineData("""{"apiKey":"sk-test","baseUrl":"https://api.example","model":""}""", "model")]
    public async Task Invalid_llm_422(string json, string field)
    {
        var ct = CancellationToken.None;
        var token = await AdminTokenAsync(ct);

        using var response = await _api.SendAsync(
            HttpMethod.Put,
            $"{SecretsPath}/llm",
            token,
            json,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var body = await CatalogApi.ReadJsonAsync(response, ct);
        body.GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == field);
    }

    [Fact]
    public async Task Proxy_reference_must_point_to_proxy_secret_422()
    {
        var ct = CancellationToken.None;
        var token = await AdminTokenAsync(ct);

        var sessionId = Guid.NewGuid();
        using (var response = await PutTelegramSessionAsync(
            token,
            sessionId,
            $"Сессия {CatalogApi.NewSuffix()}",
            $"SESSION-{CatalogApi.NewSuffix()}",
            proxyId: null,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using (var response = await PutTelegramSessionAsync(
            token,
            Guid.NewGuid(),
            $"Сессия {CatalogApi.NewSuffix()}",
            $"SESSION-{CatalogApi.NewSuffix()}",
            sessionId,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("errors").EnumerateArray()
                .ShouldContain(error => error.GetProperty("field").GetString() == "proxyId");
        }

        using (var response = await PutLlmAsync(
            token,
            $"sk-{CatalogApi.NewSuffix()}",
            ct,
            proxyId: sessionId))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("errors").EnumerateArray()
                .ShouldContain(error => error.GetProperty("field").GetString() == "proxyId");
        }
    }

    private Task<string> AdminTokenAsync(CancellationToken ct) =>
        TestAuth.CreateAccessTokenAsync(_api.Factory.Services, AdminId, ct);

    private Task<HttpResponseMessage> PutTelegramSessionAsync(
        string? token,
        Guid id,
        string name,
        string session,
        Guid? proxyId,
        CancellationToken ct,
        string? mode = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["session"] = session,
            ["proxyId"] = proxyId,
        };
        if (mode is not null)
        {
            body["mode"] = mode;
        }

        return _api.SendAsync(
            HttpMethod.Put,
            $"{SecretsPath}/telegram-sessions/{id}",
            token,
            JsonSerializer.Serialize(body),
            ct);
    }

    private Task<HttpResponseMessage> PutProxyAsync(
        string? token,
        Guid id,
        string name,
        string host,
        CancellationToken ct,
        string? username = "proxy-user",
        string? password = "proxy-password") =>
        _api.SendAsync(
            HttpMethod.Put,
            $"{SecretsPath}/proxies/{id}",
            token,
            JsonSerializer.Serialize(new
            {
                name,
                scheme = "socks5",
                host,
                port = 1080,
                username,
                password,
            }),
            ct);

    private Task<HttpResponseMessage> PutLlmAsync(
        string? token,
        string apiKey,
        CancellationToken ct,
        Guid? proxyId = null) =>
        _api.SendAsync(
            HttpMethod.Put,
            $"{SecretsPath}/llm",
            token,
            JsonSerializer.Serialize(new
            {
                apiKey,
                baseUrl = "https://api.example/v1",
                model = "gpt-test",
                proxyId,
            }),
            ct);

    private async Task<Guid> CreateProxyAsync(string token, string host, CancellationToken ct)
    {
        var proxyId = Guid.NewGuid();

        using var response = await PutProxyAsync(
            token,
            proxyId,
            $"Прокси {CatalogApi.NewSuffix()}",
            host,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        return proxyId;
    }

    private async Task<Guid> CreateMaxSourceAsync(string token, CancellationToken ct)
    {
        var suffix = CatalogApi.NewSuffix();

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            SourcesPath,
            token,
            JsonSerializer.Serialize(new
            {
                name = $"MAX-источник {suffix}",
                sourceType = "max_chat",
                chatId = $"@max_{suffix}",
                timeoutSeconds = 3600,
                userPrompt = string.Empty,
            }),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
    }

    private ServiceProvider BuildWorkerProvider()
    {
        var cipher = _api.Factory.Services.GetRequiredService<ISecretCipher>();

        return new ServiceCollection()
            .AddSingleton(cipher)
            .AddPersistence(fixture.ConnectionString)
            .AddIdentityAccess()
            .AddSources()
            .AddSecretReader()
            .BuildServiceProvider();
    }

    private static string ConnectionJson(Guid? telegramSessionId, Guid? proxyId) =>
        JsonSerializer.Serialize(new { telegramSessionId, proxyId });

    private async Task<(int KeyVersion, byte[] Ciphertext)> ReadCiphertextAsync(
        Guid secretId,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            "select key_version, ciphertext from secrets where id = @id",
            connection);
        command.Parameters.AddWithValue("id", secretId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        (await reader.ReadAsync(ct)).ShouldBeTrue();

        return (reader.GetInt32(0), reader.GetFieldValue<byte[]>(1));
    }

    private async Task<(Guid Id, int KeyVersion, byte[] Ciphertext)> ReadLlmSecretAsync(
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            "select id, key_version, ciphertext from secrets where kind = 'llm'",
            connection);

        await using var reader = await command.ExecuteReaderAsync(ct);
        (await reader.ReadAsync(ct)).ShouldBeTrue();

        return (reader.GetGuid(0), reader.GetInt32(1), reader.GetFieldValue<byte[]>(2));
    }

    private async Task<AuditSnapshot?> ReadLatestAuditAsync(Guid entityId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select action, entity_type, before::text, after::text
            from audit_entries
            where entity_id = @entity_id
            order by id desc
            limit 1
            """,
            connection);
        command.Parameters.AddWithValue("entity_id", entityId.ToString());

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new AuditSnapshot(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private async Task<long> CountAsync(string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(sql, connection);

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(ct),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record AuditSnapshot(
        string Action,
        string? EntityType,
        string? Before,
        string? After);
}
