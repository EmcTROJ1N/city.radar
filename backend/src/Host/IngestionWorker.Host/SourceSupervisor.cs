using System.Collections.Concurrent;

using Ingestion.Abstractions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sources.Contracts;

namespace IngestionWorker.Host;

/// <summary>
/// Supervisor адаптеров источников.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ApplyAsync"/> сверяет текущий набор включённых источников с запущенными адаптерами:
/// новый источник запускается, изменённый (другой хеш конфигурации) перезапускается, отключённый
/// или удалённый — останавливается. Перезапускается только затронутый источник.
/// </para>
/// <para>
/// Каждый адаптер живёт в своей задаче: падение одного (исключение в потоке сообщений или
/// в запуске) изолировано <c>try/catch</c>, пишется в health источника и приводит к перезапуску
/// с экспоненциальным backoff — остальные источники продолжают работать.
/// </para>
/// <para>
/// Для типа источника без зарегистрированной <see cref="ISourceAdapterFactory"/> источник
/// получает health-ошибку «адаптер не реализован»; Worker при этом не падает.
/// </para>
/// <para>
/// Один тип источника может поддерживаться несколькими фабриками (у <c>telegram_chat</c> —
/// юзербот MTProto и Bot API). Фабрики перебираются по порядку регистрации, выбирается первая,
/// у которой <see cref="ISourceAdapterFactory.CanHandle"/> вернул <c>true</c>.
/// </para>
/// </remarks>
public sealed class SourceSupervisor : IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlyList<ISourceAdapterFactory> _factories;
    private readonly ProxyResolver _proxyResolver;
    private readonly WorkerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SourceSupervisor> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, RunningSource> _running = new();
    private readonly Dictionary<Guid, string> _unsupported = [];

    private bool _disposed;

    /// <summary>Создаёт supervisor поверх зарегистрированных фабрик адаптеров.</summary>
    public SourceSupervisor(
        IServiceScopeFactory scopeFactory,
        IEnumerable<ISourceAdapterFactory> factories,
        ProxyResolver proxyResolver,
        WorkerOptions options,
        TimeProvider timeProvider,
        ILogger<SourceSupervisor> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(factories);
        ArgumentNullException.ThrowIfNull(proxyResolver);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _proxyResolver = proxyResolver;

        // Порядок регистрации важен: несколько фабрик могут обслуживать один тип источника
        // (telegram_chat: bot и userbot), выбирается первая подходящая по CanHandle.
        _factories = [.. factories];
    }

    /// <summary>Идентификаторы источников, чьи адаптеры сейчас запущены (для диагностики и тестов).</summary>
    public IReadOnlyCollection<Guid> RunningSourceIds
    {
        get => _running.Keys.ToArray();
    }

    /// <summary>
    /// Приводит запущенные адаптеры в соответствие со снимком конфигурации источников.
    /// </summary>
    /// <param name="configs">Включённые источники с хешами конфигурации.</param>
    /// <param name="ct">Токен отмены.</param>
    public async Task ApplyAsync(
        IReadOnlyList<SourceConfigSnapshot> configs,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(configs);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var enabled = new Dictionary<Guid, SourceConfigSnapshot>(configs.Count);
            foreach (var config in configs)
            {
                enabled[config.SourceId] = config;
            }

            // Отключённые и удалённые источники останавливаются.
            foreach (var sourceId in _running.Keys.Where(id => !enabled.ContainsKey(id)).ToArray())
            {
                await StopAsync(sourceId, ct).ConfigureAwait(false);
            }

            foreach (var sourceId in _unsupported.Keys.Where(id => !enabled.ContainsKey(id)).ToArray())
            {
                _unsupported.Remove(sourceId);
            }

            foreach (var config in configs)
            {
                if (_running.TryGetValue(config.SourceId, out var running) &&
                    string.Equals(running.Hash, config.Hash, StringComparison.Ordinal))
                {
                    continue;
                }

                if (running is not null)
                {
                    await StopAsync(config.SourceId, ct).ConfigureAwait(false);
                }

                if (!SupportsSource(config))
                {
                    if (_unsupported.TryGetValue(config.SourceId, out var previousHash) &&
                        string.Equals(previousHash, config.Hash, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    _unsupported[config.SourceId] = config.Hash;

                    await ReportHealthAsync(
                        config.SourceId,
                        SourceState.Error,
                        $"Адаптер для типа источника \"{config.SourceType}\" не реализован.",
                        lastMessageAt: null,
                        ct).ConfigureAwait(false);

                    _logger.LogWarning(
                        "Worker: для типа источника '{SourceType}' нет фабрики адаптера (источник {SourceId}).",
                        config.SourceType,
                        config.SourceId);

                    continue;
                }

                _unsupported.Remove(config.SourceId);

                Start(config);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Запускает задачу обслуживания одного источника.</summary>
    private void Start(SourceConfigSnapshot config)
    {
        var cancellation = new CancellationTokenSource();
        var loop = Task.Run(() => RunAsync(config, cancellation.Token), CancellationToken.None);

        _running[config.SourceId] = new RunningSource(config.Hash, cancellation, loop);

        _logger.LogInformation(
            "Worker: запущен адаптер источника {SourceId} ({SourceType}).",
            config.SourceId,
            config.SourceType);
    }

    /// <summary>
    /// Обслуживает один источник: создаёт адаптер, читает сообщения и перезапускает адаптер
    /// после ошибки с backoff, пока источник не остановлен.
    /// </summary>
    private async Task RunAsync(SourceConfigSnapshot config, CancellationToken ct)
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            IDataSource? adapter = null;
            try
            {
                adapter = await CreateAdapterAsync(config, ct).ConfigureAwait(false);
                await adapter.StartAsync(ct).ConfigureAwait(false);

                attempt = 0;

                // Health в состоянии Ok пишет сам адаптер: supervisor не должен
                // перетирать auth_error/flood_wait, которые адаптер мог выставить при запуске.
                await ConsumeAsync(config.SourceId, adapter, ct).ConfigureAwait(false);

                // Поток сообщений завершился без отмены: это сбой адаптера, нужен перезапуск.
                attempt++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                attempt++;

                _logger.LogWarning(
                    exception,
                    "Worker: ошибка адаптера источника {SourceId}; перезапуск через backoff.",
                    config.SourceId);

                await ReportHealthAsync(
                    config.SourceId,
                    SourceState.Error,
                    $"Ошибка адаптера: {exception.Message}",
                    lastMessageAt: null,
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (adapter is not null)
                {
                    await StopAdapterAsync(config.SourceId, adapter).ConfigureAwait(false);
                }
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(Backoff(attempt), _timeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Создаёт адаптер через подходящую фабрику, подставляя сессию и прокси из БД.</summary>
    private async Task<IDataSource> CreateAdapterAsync(
        SourceConfigSnapshot snapshot,
        CancellationToken ct)
    {
        ResolvedConnection? connection;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var resolver = scope.ServiceProvider.GetRequiredService<ISourceConnectionResolver>();
            connection = await resolver.ResolveAsync(snapshot.SourceId, ct).ConfigureAwait(false);
        }

        var connectionProxy = connection?.Proxy is { } info
            ? new ProxySettings(info.Scheme, info.Host, info.Port, info.Username, info.Password)
            : null;

        // ISourceConnectionResolver уже разрешил цепочку «источник → аккаунт → глобальный»
        //; ProxyResolver — единая точка правила, через которую прокси попадает
        // в конфигурацию адаптера, чтобы обойти её было нельзя.
        var proxy = _proxyResolver.Resolve(
            new SourceRuntimeConfig(
                snapshot.SourceId,
                snapshot.SourceType,
                snapshot.Settings,
                SessionSecret: null,
                connectionProxy),
            globalProxy: null);

        var runtimeConfig = new SourceRuntimeConfig(
            snapshot.SourceId,
            snapshot.SourceType,
            snapshot.Settings,
            connection?.SessionSecret,
            proxy,
            connection?.ProxyRequired ?? false,
            (state, detail, lastMessageAt, token) => ReportHealthAsync(
                snapshot.SourceId,
                state,
                detail,
                lastMessageAt,
                token));

        var factory = FindFactory(runtimeConfig)
            ?? throw new NotSupportedException(
                $"Адаптер для типа источника \"{snapshot.SourceType}\" не реализован.");

        return factory.Create(runtimeConfig);
    }

    /// <summary>Есть ли фабрика, готовая обслуживать источник (проверка без разрешения секретов).</summary>
    private bool SupportsSource(SourceConfigSnapshot snapshot) =>
        FindFactory(new SourceRuntimeConfig(
            snapshot.SourceId,
            snapshot.SourceType,
            snapshot.Settings,
            SessionSecret: null,
            Proxy: null)) is not null;

    /// <summary>Первая фабрика по порядку регистрации, которая берётся за источник.</summary>
    private ISourceAdapterFactory? FindFactory(SourceRuntimeConfig config)
    {
        foreach (var factory in _factories)
        {
            if (factory.CanHandle(config))
            {
                return factory;
            }
        }

        return null;
    }

    /// <summary>
    /// Читает поток сообщений адаптера и передаёт их в обработку. Ошибка обработки одного
    /// сообщения не роняет поток; отмена пробрасывается наружу.
    /// </summary>
    private async Task ConsumeAsync(Guid sourceId, IDataSource adapter, CancellationToken ct)
    {
        await foreach (var message in adapter.ReadAsync(ct).WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var ingestion = scope.ServiceProvider.GetRequiredService<MessageIngestionService>();

                await ingestion.HandleAsync(message, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Сообщение падает изолированно: остальные сообщения и источники не страдают.
                _logger.LogError(
                    exception,
                    "Worker: ошибка обработки сообщения {ExternalId} источника {SourceId}.",
                    message.ExternalId,
                    sourceId);

                await ReportHealthAsync(
                    sourceId,
                    SourceState.Error,
                    $"Ошибка обработки сообщения: {exception.Message}",
                    lastMessageAt: null,
                    ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Останавливает адаптер; ошибка остановки не должна мешать перезапуску.</summary>
    private async Task StopAdapterAsync(Guid sourceId, IDataSource adapter)
    {
        try
        {
            await adapter.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Worker: ошибка остановки адаптера источника {SourceId}.",
                sourceId);
        }

        try
        {
            await adapter.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Worker: ошибка освобождения адаптера источника {SourceId}.",
                sourceId);
        }
    }

    /// <summary>Останавливает адаптер источника и дожидается завершения его задачи.</summary>
    private async Task StopAsync(Guid sourceId, CancellationToken ct)
    {
        if (!_running.TryRemove(sourceId, out var running))
        {
            return;
        }

        await running.Cancellation.CancelAsync().ConfigureAwait(false);

        try
        {
            await running.Loop.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Отмена ожидания (остановка хоста) не должна оставлять задачу адаптера висящей:
            // отмена уже запрошена, дожидаемся её завершения без токена.
            try
            {
                await running.Loop.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Worker: задача адаптера источника {SourceId} завершилась с ошибкой.",
                    sourceId);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Worker: задача адаптера источника {SourceId} завершилась с ошибкой.",
                sourceId);
        }
        finally
        {
            running.Cancellation.Dispose();
        }

        _logger.LogInformation("Worker: остановлен адаптер источника {SourceId}.", sourceId);
    }

    /// <summary>Задержка перезапуска: экспонента с верхней границей из настроек.</summary>
    private TimeSpan Backoff(int attempt)
    {
        var baseDelay = _options.RestartBackoffBase;
        var max = _options.RestartBackoffMax;
        var factor = Math.Pow(2, Math.Min(Math.Max(attempt - 1, 0), 10));

        var delay = TimeSpan.FromTicks((long)Math.Min(baseDelay.Ticks * factor, max.Ticks));

        return delay < baseDelay ? baseDelay : delay;
    }

    /// <summary>Пишет health источника через scoped-репортёр; сбой записи не роняет supervisor.</summary>
    private async Task ReportHealthAsync(
        Guid sourceId,
        SourceState state,
        string? detail,
        DateTimeOffset? lastMessageAt,
        CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var reporter = scope.ServiceProvider.GetRequiredService<ISourceHealthReporter>();

            await reporter.ReportAsync(sourceId, state, detail, lastMessageAt, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Worker: не удалось записать health источника {SourceId}.",
                sourceId);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var sourceId in RunningSourceIds)
        {
            await StopAsync(sourceId, CancellationToken.None).ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    /// <summary>Запущенный источник: хеш конфигурации, отмена задачи и сама задача обслуживания.</summary>
    private sealed record RunningSource(string Hash, CancellationTokenSource Cancellation, Task Loop);
}
