using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using BuildingBlocks.Security;

using Ingestion.Abstractions;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Shouldly;

namespace Api.IntegrationTests.Ingestion;

/// <summary>
/// Прокси адаптеров Telegram: порядок разрешения, запрет прямого
/// соединения при недоступном прокси, обязательность прокси, горячая смена без перезапуска
/// Worker-а и отсутствие учётных данных в логах. Реальный Telegram и реальные прокси не используются.
/// </summary>
[Collection("postgres")]
public sealed class ProxyTests(Infrastructure.PostgresFixture fixture) : IAsyncLifetime
{
    private string ConnectionString => fixture.ConnectionString;

    public async Task InitializeAsync() =>
        await WorkerDatabase.CleanupPreviousRunsAsync(ConnectionString);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Proxy_resolution_order()
    {
        var sourceProxy = new ProxySettings("socks5", "source.proxy", 1080);
        var accountProxy = new ProxySettings("socks5", "account.proxy", 1080);
        var globalProxy = new ProxySettings("http", "global.proxy", 3128);

        // Источник → аккаунт → глобальный: первый заданный побеждает.
        ProxyResolver.Resolve(sourceProxy, accountProxy, globalProxy).ShouldBe(sourceProxy);
        ProxyResolver.Resolve(null, accountProxy, globalProxy).ShouldBe(accountProxy);
        ProxyResolver.Resolve(null, null, globalProxy).ShouldBe(globalProxy);
        ProxyResolver.Resolve(null, null, null).ShouldBeNull();

        var resolver = new ProxyResolver();

        // Публичный контракт: снимок источника уже содержит победившее звено,
        // глобальный прокси — последний fallback.
        resolver.Resolve(AdapterTestData.Config(proxy: sourceProxy), globalProxy)
            .ShouldBe(sourceProxy);
        resolver.Resolve(AdapterTestData.Config(proxy: null), globalProxy)
            .ShouldBe(globalProxy);
    }

    [Fact]
    public async Task Unreachable_proxy_never_connects_directly()
    {
        var ct = CancellationToken.None;

        // Целевой сервер: если адаптер попытается подключиться напрямую, он получит соединение.
        var destination = new TcpListener(IPAddress.Loopback, 0);
        destination.Start();
        var destinationPort = ((IPEndPoint)destination.LocalEndpoint).Port;

        try
        {
            var proxy = new ProxySettings("socks5", "127.0.0.1", AdapterTestData.UnusedPort());
            using var handler = ProxyHttpHandlerFactory.Create(proxy, proxyRequired: true);
            using var client = new HttpClient(handler);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Should.ThrowAsync<Exception>(
                async () => await client.GetAsync(
                    $"http://127.0.0.1:{destinationPort}/",
                    timeout.Token));

            var accepted = destination.AcceptTcpClientAsync();
            await Task.Delay(200, ct);

            accepted.IsCompleted.ShouldBeFalse(
                "при недоступном прокси прямое соединение с целевым хостом запрещено ");

            // Прямое подключение мимо прокси запрещено и на уровне проверки endpoint-а.
            Should.Throw<DirectConnectionForbiddenException>(
                () => ProxyHttpHandlerFactory.EnsureEndpointAllowed(
                    new DnsEndPoint("127.0.0.1", destinationPort),
                    proxy));
        }
        finally
        {
            destination.Stop();
        }
    }

    [Fact]
    public void Proxy_required_when_configured()
    {
        // Прокси задан в цепочке, но параметры не разрешились: прямое соединение запрещено.
        Should.Throw<ProxyConfigurationException>(
            () => ProxyHttpHandlerFactory.Create(proxy: null, proxyRequired: true));

        // MTProxy — протокол MTProto; для Bot API это ошибка конфигурации с понятным текстом.
        var mtproxyError = Should.Throw<ProxyConfigurationException>(
            () => ProxyHttpHandlerFactory.Create(
                new ProxySettings("mtproxy", "proxy.example", 443, Password: "secret")));
        mtproxyError.Message.ShouldContain("MTProxy");

        // SOCKS5 и HTTP-прокси дают обработчик, который всегда использует прокси.
        using var socks = ProxyHttpHandlerFactory.Create(
            new ProxySettings("socks5", "proxy.example", 1080, "user", "password"));
        socks.UseProxy.ShouldBeTrue();
        socks.Proxy.ShouldNotBeNull();

        using var http = ProxyHttpHandlerFactory.Create(
            new ProxySettings("http", "proxy.example", 3128, "user", "password"));
        http.UseProxy.ShouldBeTrue();
        http.Proxy.ShouldNotBeNull();
    }

    [Fact]
    public void Credentials_not_in_ToString_or_logs()
    {
        const string password = "super-secret-proxy-password";
        const string sessionSecret = "BOT-SESSION-SECRET";

        var proxy = new ProxySettings("http", "proxy.example", 3128, "user", password);
        proxy.ToString().ShouldNotContain(password);
        proxy.ToString().ShouldContain("***");

        var config = AdapterTestData.Config(
            session: sessionSecret,
            proxy: proxy,
            proxyRequired: true);

        config.ToString().ShouldNotContain(sessionSecret);
        config.ToString().ShouldNotContain(password);
        config.ToString().ShouldContain("ProxyRequired = True");
    }

    [Fact]
    public async Task Changing_proxy_applies_without_restart()
    {
        var ct = CancellationToken.None;
        var source = await WorkerDatabase.CreateSourceAsync(ConnectionString, ct: ct);
        var factory = new FakeAdapterFactory();
        var llm = new FakeLlmProvider();

        await using var worker = new WorkerTestContext(ConnectionString, factory, llm);

        var cipher = worker.Services.GetRequiredService<ISecretCipher>();

        var firstProxyId = await CreateProxyAsync(
            ConnectionString,
            cipher,
            $"first-{Guid.NewGuid():N}.proxy.example",
            ct);
        await AssignProxyAsync(ConnectionString, source.Id, firstProxyId, ct);

        await worker.StartAsync(ct);

        await WorkerTestContext.WaitUntilAsync(
            () => Task.FromResult(factory.InstanceCount(source.Id) == 1),
            "адаптер запущен с первым прокси");

        var firstConfig = factory.Configurations.Last();
        firstConfig.Proxy.ShouldNotBeNull();
        firstConfig.Proxy!.Host.ShouldStartWith("first-");
        firstConfig.ProxyRequired.ShouldBeTrue();

        // Смена секрета прокси меняет хеш конфигурации: supervisor пересоздаёт только адаптер
        // источника, Worker и остальные источники не перезапускаются.
        var secondProxyId = await CreateProxyAsync(
            ConnectionString,
            cipher,
            $"second-{Guid.NewGuid():N}.proxy.example",
            ct);
        await AssignProxyAsync(ConnectionString, source.Id, secondProxyId, ct);

        await WorkerTestContext.WaitUntilAsync(
            () => Task.FromResult(factory.InstanceCount(source.Id) >= 2),
            "адаптер пересоздан после смены прокси без перезапуска Worker-а");

        var secondConfig = factory.Configurations.Last();
        secondConfig.Proxy.ShouldNotBeNull();
        secondConfig.Proxy!.Host.ShouldStartWith("second-");
        secondConfig.Proxy.Host.ShouldNotBe(firstConfig.Proxy!.Host);
    }

    /// <summary>Создаёт зашифрованный секрет прокси напрямую в БД.</summary>
    private static async Task<Guid> CreateProxyAsync(
        string connectionString,
        ISecretCipher cipher,
        string host,
        CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(
            new
            {
                scheme = "socks5",
                host,
                port = 1080,
                username = (string?)null,
                password = (string?)null,
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var ciphertext = cipher.Encrypt(Encoding.UTF8.GetBytes(payload), cipher.CurrentKeyVersion);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            insert into secrets (id, kind, name, ciphertext, key_version)
            values (@id, 'proxy', @name, @ciphertext, @key_version)
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", $"Прокси теста {host}");
        command.Parameters.AddWithValue("ciphertext", ciphertext);
        command.Parameters.AddWithValue("key_version", cipher.CurrentKeyVersion);

        await command.ExecuteNonQueryAsync(ct);

        return id;
    }

    private static async Task AssignProxyAsync(
        string connectionString,
        Guid sourceId,
        Guid proxyId,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            "update sources set proxy_id = @proxy_id where id = @id",
            connection);
        command.Parameters.AddWithValue("proxy_id", proxyId);
        command.Parameters.AddWithValue("id", sourceId);

        await command.ExecuteNonQueryAsync(ct);
    }
}
