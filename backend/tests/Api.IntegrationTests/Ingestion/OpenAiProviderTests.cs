using System.Net;
using System.Net.Sockets;
using System.Text;

using Ingestion.Abstractions;
using Ingestion.Llm.OpenAi;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Shouldly;

using Sources.Contracts;

namespace Api.IntegrationTests.Ingestion;

/// <summary>
/// Провайдер LLM: ключ и модель применяются без перезапуска
/// (кэш 30 секунд), ключ не попадает в журнал, 429 повторяется один раз, прокси при заданном
/// ProxyUrl обязателен и прямого подключения нет. Сеть подменена <see cref="HttpMessageHandler"/>.
/// </summary>
public sealed class OpenAiProviderTests
{
    private const string ApiKey = "sk-test-super-secret-key-do-not-log";
    private const string ChatCompletion =
        """
        {"choices":[{"message":{"role":"assistant","content":"{\"relevant\":false}"}}]}
        """;

    private static readonly LlmRequest Request = new("системный промпт", "промпт пользователя", "текст сообщения");

    [Fact]
    public async Task Key_is_not_written_to_logs()
    {
        var logger = new RecordingLogger<OpenAiProvider>();
        var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.OK, ChatCompletion);
        handler.Enqueue(HttpStatusCode.InternalServerError, "server error");
        handler.Enqueue(HttpStatusCode.InternalServerError, "server error");

        using var provider = CreateProvider(logger, handler, Settings(ApiKey));

        await provider.CompleteAsync(Request, CancellationToken.None);

        var failure = await Should.ThrowAsync<HttpRequestException>(
            () => provider.CompleteAsync(Request, CancellationToken.None));

        failure.Message.ShouldNotContain(ApiKey);
        logger.Messages.ShouldNotBeEmpty();
        foreach (var message in logger.Messages)
        {
            message.ShouldNotContain(ApiKey);
            message.ShouldNotContain("текст сообщения");
        }
    }

    [Fact]
    public async Task Retries_once_on_429()
    {
        var logger = new RecordingLogger<OpenAiProvider>();
        var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.TooManyRequests, "slow down");
        handler.Enqueue(HttpStatusCode.OK, ChatCompletion);

        using var provider = CreateProvider(logger, handler, Settings(ApiKey));

        var result = await provider.CompleteAsync(Request, CancellationToken.None);

        result.ShouldBe("{\"relevant\":false}");
        handler.Requests.Count.ShouldBe(2);
        handler.Authorizations.ShouldAllBe(value => value == ApiKey);

        // Запрос соответствует контракту: детерминированная температура и JSON-режим.
        handler.Bodies[0].ShouldContain("\"temperature\":0");
        handler.Bodies[0].ShouldContain("\"json_object\"");
        handler.Bodies[0].ShouldContain("\"model\":\"gpt-4o-mini\"");
    }

    [Fact]
    public async Task Settings_cached_for_30_seconds_and_reread_after_ttl()
    {
        var logger = new RecordingLogger<OpenAiProvider>();
        var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.OK, ChatCompletion);
        handler.Enqueue(HttpStatusCode.OK, ChatCompletion);
        handler.Enqueue(HttpStatusCode.OK, ChatCompletion);

        var secrets = new StubLlmSettingsSource(
            Settings("key-first"),
            Settings("key-second"),
            Settings("key-second"));

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));

        using var monitor = new OpenAiOptionsMonitor(secrets, time, TimeSpan.FromSeconds(30));
        using var provider = new OpenAiProvider(
            monitor,
            logger,
            handler,
            TimeSpan.FromSeconds(5),
            TimeSpan.Zero);

        await provider.CompleteAsync(Request, CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(29));
        await provider.CompleteAsync(Request, CancellationToken.None);

        secrets.ReadCount.ShouldBe(1);
        handler.Authorizations.ShouldAllBe(value => value == "key-first");

        // Ровно на границе 30 секунд кэш истёк: новый ключ применяется без перезапуска.
        time.Advance(TimeSpan.FromSeconds(1));
        await provider.CompleteAsync(Request, CancellationToken.None);

        secrets.ReadCount.ShouldBe(2);
        handler.Authorizations[^1].ShouldBe("key-second");
    }

    [Fact]
    public async Task Proxy_is_mandatory_when_configured_and_direct_connection_is_not_used()
    {
        // Слушатель изображает прямой канал к LLM: если провайдер пойдёт напрямую, он получит
        // соединение, и счётчик это зафиксирует. Прокси указывает на закрытый порт.
        var directListener = new TcpListener(IPAddress.Loopback, 0);
        directListener.Start();

        var directPort = ((IPEndPoint)directListener.LocalEndpoint).Port;
        var directConnections = 0;

        using var stop = new CancellationTokenSource();

        var acceptLoop = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var client = await directListener.AcceptTcpClientAsync(stop.Token);
                    Interlocked.Increment(ref directConnections);
                    client.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
        });

        var closedPort = FindClosedPort();
        var settings = new LlmSettings(
            ApiKey,
            $"http://127.0.0.1:{directPort}/v1",
            "gpt-4o-mini",
            $"http://127.0.0.1:{closedPort}");

        try
        {
            using var provider = new OpenAiProvider(
                new OpenAiOptionsMonitor(new StubLlmSettingsSource(settings), TimeProvider.System),
                new RecordingLogger<OpenAiProvider>(),
                handler: null,
                TimeSpan.FromSeconds(5),
                TimeSpan.Zero);

            await Should.ThrowAsync<Exception>(
                () => provider.CompleteAsync(Request, CancellationToken.None));

            await Task.Delay(200);

            Volatile.Read(ref directConnections).ShouldBe(0, "при заданном прокси прямое подключение запрещено ");
        }
        finally
        {
            await stop.CancelAsync();
            directListener.Stop();
            await acceptLoop;
        }
    }

    [Fact]
    public async Task Registered_provider_does_not_capture_scoped_secret_reader()
    {
        // Кэш настроек — singleton, ISecretReader — scoped. Ошибка захвата scope ломала бы
        // ValidateScopes в Development и удерживала бы DbContext на весь процесс.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ISecretReader>(_ => new StubSecretReader(Settings("key-in-di")));
        services.AddOpenAiProvider();

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        var monitor = provider.GetRequiredService<OpenAiOptionsMonitor>();
        var settings = await monitor.GetAsync(CancellationToken.None);

        settings.ShouldNotBeNull();
        settings!.ApiKey.ShouldBe("key-in-di");
    }

    private static OpenAiProvider CreateProvider(
        RecordingLogger<OpenAiProvider> logger,
        RecordingHandler handler,
        LlmSettings settings) =>
        new(
            new OpenAiOptionsMonitor(new StubLlmSettingsSource(settings), TimeProvider.System),
            logger,
            handler,
            TimeSpan.FromSeconds(5),
            TimeSpan.Zero);

    private static LlmSettings Settings(string apiKey) =>
        new(apiKey, "https://llm.example/v1", "gpt-4o-mini", ProxyUrl: null);

    /// <summary>Берёт свободный порт и сразу освобождает его — соединение на нём будет отклонено.</summary>
    private static int FindClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }

    /// <summary>Источник настроек LLM с заданной последовательностью ответов.</summary>
    private sealed class StubLlmSettingsSource(params LlmSettings?[] settings) : ILlmSettingsSource
    {
        private readonly Queue<LlmSettings?> _settings = new(settings);

        public int ReadCount { get; private set; }

        public Task<LlmSettings?> ReadAsync(CancellationToken ct)
        {
            ReadCount++;

            return Task.FromResult(_settings.Count > 0 ? _settings.Dequeue() : null);
        }
    }

    /// <summary>Scoped-чтение секрета для проверки DI: singleton-кэш не захватывает scope.</summary>
    private sealed class StubSecretReader(LlmSettings settings) : ISecretReader
    {
        public Task<string?> ReadAsync(Guid secretId, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task<LlmSettings?> ReadLlmAsync(CancellationToken ct) =>
            Task.FromResult<LlmSettings?>(settings);
    }

    /// <summary>HTTP-транспорт с заготовленными ответами и записью запросов.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string?> Authorizations { get; } = [];

        public List<string> Bodies { get; } = [];

        public void Enqueue(HttpStatusCode status, string body) =>
            _responses.Enqueue(_ => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Authorizations.Add(request.Headers.Authorization?.Parameter);
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("Нет заготовленного ответа HTTP-заглушки.");
            }

            return _responses.Dequeue()(request);
        }
    }
}
