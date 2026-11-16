using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IngestionWorker.Host;

/// <summary>
/// Фоновый опрос настроек источников.
/// </summary>
/// <remarks>
/// Каждые <see cref="WorkerOptions.ConfigPollInterval"/> секунд (по умолчанию 10) читается
/// конфигурация всех включённых источников и считается её хеш. Изменения передаются
/// <see cref="SourceSupervisor"/>, который перезапускает только затронутый адаптер.
/// Ошибка опроса (БД недоступна) журналируется и не останавливает цикл.
/// </remarks>
public sealed class SourceConfigWatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SourceSupervisor _supervisor;
    private readonly WorkerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SourceConfigWatcher> _logger;

    /// <summary>Создаёт watcher поверх supervisor-а и чтения настроек из БД.</summary>
    public SourceConfigWatcher(
        IServiceScopeFactory scopeFactory,
        SourceSupervisor supervisor,
        WorkerOptions options,
        TimeProvider timeProvider,
        ILogger<SourceConfigWatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _supervisor = supervisor;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Один цикл: читает конфигурацию источников и применяет изменения. Публичный метод нужен
    /// фоновому циклу и тестам, которым важно детерминированное применение настроек.
    /// </summary>
    public async Task PollOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<SourceConfigSnapshot> configs;

        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var reader = scope.ServiceProvider.GetRequiredService<SourceConfigReader>();
            configs = await reader.ReadAsync(ct).ConfigureAwait(false);
        }

        await _supervisor.ApplyAsync(configs, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.ConfigPollInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Worker: ошибка опроса конфигурации источников.");
            }

            try
            {
                await Task.Delay(interval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
