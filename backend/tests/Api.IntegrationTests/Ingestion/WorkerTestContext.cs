using BuildingBlocks.Messaging;
using BuildingBlocks.Persistence;
using BuildingBlocks.Security;

using Catalog.Core;

using IdentityAccess.Core;

using Ingestion.Abstractions;

using IngestionWorker.Host;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Sources.Contracts;
using Sources.Core;
using Sources.Core.Secrets;

namespace Api.IntegrationTests.Ingestion;

/// <summary>
/// Собственный ServiceProvider Worker-а на настоящей БД <see cref="PostgresFixture"/>:
/// composition root повторяет <c>IngestionWorker.Host</c>, но провайдер LLM и фабрика адаптеров
/// подменены fake-реализациями. Позволяет проверять горячее применение настроек без сети и
/// без AspNetCore.
/// </summary>
internal sealed class WorkerTestContext : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly SourceConfigWatcher _watcher;
    private bool _started;

    public WorkerTestContext(
        string connectionString,
        FakeAdapterFactory adapterFactory,
        FakeLlmProvider llm,
        Action<IServiceCollection>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(adapterFactory);
        ArgumentNullException.ThrowIfNull(llm);

        AdapterFactory = adapterFactory;
        Llm = llm;

        var services = new ServiceCollection();

        services.AddLogging();
        services.AddPersistence(connectionString);
        services.AddSecretCipher(new TestHostEnvironment("Testing"));
        services.AddIdentityAccess();
        services.AddCatalog();
        services.AddSources();
        services.AddOutbox();
        services.AddSecretReader();
        services.AddSourceHealthReporter();

        services.AddSingleton<ILlmProvider>(llm);

        // Fake-фабрика регистрируется раньше worker-а: supervisor перебирает фабрики по порядку,
        // и тестовая должна перехватить telegram_chat раньше реальных адаптеров.
        services.AddSingleton<ISourceAdapterFactory>(adapterFactory);

        var options = new WorkerOptions
        {
            ConfigPollSeconds = 0.02,
            RestartBackoffSeconds = 0.01,
            RestartBackoffMaxSeconds = 0.05,
        };
        services.AddIngestionWorker(options);

        configure?.Invoke(services);

        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });

        _watcher = _provider.GetRequiredService<SourceConfigWatcher>();
        Supervisor = _provider.GetRequiredService<SourceSupervisor>();
    }

    public IServiceProvider Services => _provider;

    public SourceSupervisor Supervisor { get; }

    public FakeAdapterFactory AdapterFactory { get; }

    public FakeLlmProvider Llm { get; }

    /// <summary>Запускает фоновый опрос настроек (интервал — 20 мс из <see cref="WorkerOptions"/>).</summary>
    public Task StartAsync(CancellationToken ct = default) =>
        StartCoreAsync(ct);

    /// <summary>Один детерминированный опрос настроек без ожидания фонового интервала.</summary>
    public Task PollOnceAsync(CancellationToken ct = default) => _watcher.PollOnceAsync(ct);

    private async Task StartCoreAsync(CancellationToken ct)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        await _watcher.StartAsync(ct);
    }

    /// <summary>Ожидает выполнения условия; при таймауте падает с понятным сообщением.</summary>
    public static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        string because,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));

        while (DateTime.UtcNow < deadline)
        {
            if (await condition().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        throw new TimeoutException($"Не дождались условия: {because}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_started)
        {
            await _watcher.StopAsync(CancellationToken.None);
        }

        _watcher.Dispose();
        await _provider.DisposeAsync();
    }
}

/// <summary>Окружение тестового хоста: не Production, чтобы шифр секретов брался с ключом процесса.</summary>
internal sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;

    public string ApplicationName { get; set; } = "Mayak.Worker.Tests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
}
