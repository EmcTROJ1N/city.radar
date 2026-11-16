using System.Data;
using System.Data.Common;
using System.Globalization;

using BuildingBlocks.Persistence;

using IdentityAccess.Contracts;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;
using NpgsqlTypes;

using Observations.Application;

namespace Observations.Infrastructure;

/// <summary>
/// Фоновый пересчёт проекции по истечении таймаута актуальности
///). Факты неизменяемы, а <c>current_statuses</c> — вычисляемая проекция, поэтому
/// истечение наблюдений и переход Fresh → Aging обязаны попадать на карту без новых отметок.
/// </summary>
/// <remarks>
/// <para>
/// Выборка кандидатов идёт по <c>current_statuses</c> (одна строка на фиксацию) с соединением
/// по первичному ключу <c>observations.id = current_statuses.observation_id</c>: сканируются
/// только проекции, а не вся история наблюдений. Фиксация пересчитывается, если победившее
/// наблюдение уже истекло (<c>expires_at &lt;= now</c>) или его вычисленная свежесть отличается
/// от записанной. Истёкшими считаются только сохранённые <c>expires_at</c>: смена таймаута
/// источника не пересчитывает срок жизни старых наблюдений.
/// </para>
/// <para>
/// Ошибка пересчёта одной фиксации логируется и не прерывает пачку; исключение итерации не
/// останавливает <see cref="ExecuteAsync"/>. Время берётся только из <see cref="TimeProvider"/>.
/// </para>
/// </remarks>
public sealed class ExpirationSweeper : BackgroundService
{
    /// <summary>Верхняя граница фиксаций, пересчитываемых за один проход.</summary>
    private const int MaxBatchSize = 500;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ExpirationSweeperOptions _options;
    private readonly ILogger<ExpirationSweeper> _logger;

    public ExpirationSweeper(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ExpirationSweeperOptions options,
        ILogger<ExpirationSweeper> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        options.Validate();

        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Один проход: находит фиксации, проекция которых устарела по TTL или свежести, и
    /// пересчитывает их последовательно. Публичный, чтобы тесты и диагностика запускали
    /// пересчёт вручную, не дожидаясь <see cref="BackgroundService"/>.
    /// </summary>
    /// <returns>Количество успешно пересчитанных фиксаций.</returns>
    public async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var candidates = await FindCandidatesAsync(now, cancellationToken);

        var recalculated = 0;

        foreach (var mapObjectId in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Свой scope на фиксацию: контекст и изменение трекинга не накапливаются
                // на всю пачку, а падение одной фиксации не отравляет остальные.
                await using var scope = _scopeFactory.CreateAsyncScope();
                var calculator = scope.ServiceProvider.GetRequiredService<IStatusRecalculator>();

                await calculator.RecalculateAsync(mapObjectId, now, cancellationToken);

                recalculated++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Observations: не удалось пересчитать фиксацию {MapObjectId} по TTL.",
                    mapObjectId);
            }
        }

        return recalculated;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.SweeperEnabled)
        {
            _logger.LogInformation(
                "Observations: фоновый пересчёт по TTL отключён (Observations:SweeperEnabled=false).");

            return;
        }

        var interval = TimeSpan.FromSeconds(_options.SweepIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Ошибка БД или неожиданный сбой не должны убивать фоновый сервис.
                _logger.LogError(exception, "Observations: ошибка фонового пересчёта по TTL.");
            }

            try
            {
                await Task.Delay(interval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Ищет проекции, которые не отражают истечение или смену свежести победившего
    /// наблюдения. Freshness считается той же формулой, что и в <c>StatusResolver</c>:
    /// <c>now - observed_at &lt; timeout * freshRatio</c> → Fresh, иначе Aging.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> FindCandidatesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        // Доля Fresh — глобальная настройка; читается тем же scope, что и выборка.
        var settings = scope.ServiceProvider.GetRequiredService<IGlobalSettings>();
        var freshRatio = (await settings.GetAsync(cancellationToken)).FreshnessFreshRatio;

        const string Sql = """
            select cs.map_object_id
            from current_statuses cs
            join observations o on o.id = cs.observation_id
            join source_policies p on p.source_id = o.source_id
            where cs.source_status_id is not null
              and (
                    o.expires_at <= @now
                    or case
                         when @now - o.observed_at < p.timeout * @fresh_ratio then 'fresh'
                         else 'aging'
                       end <> cs.freshness
                  )
            order by cs.updated_at, cs.map_object_id
            limit @batch_size
            """;

        var db = scope.ServiceProvider.GetRequiredService<MayakDbContext>();
        var connection = db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;

        if (closeAfter)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = Sql;

            AddParameter(command, "now", NpgsqlDbType.TimestampTz, now);
            AddParameter(command, "fresh_ratio", NpgsqlDbType.Double, freshRatio);
            AddParameter(command, "batch_size", NpgsqlDbType.Integer, MaxBatchSize);

            var candidates = new List<Guid>();

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add(reader.GetGuid(0));
            }

            return candidates;
        }
        finally
        {
            if (closeAfter)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    private static void AddParameter(DbCommand command, string name, NpgsqlDbType type, object value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });
    }
}

/// <summary>
/// Настройки периодического пересчёта по TTL. Интервал и включение — опции
/// процесса Api (<c>Observations:SweepIntervalSeconds</c>, <c>Observations:SweeperEnabled</c>),
/// а не запись <c>global_settings</c>: реестр глобальных настроек не расширяется без
/// необходимости, потому что интервал не меняется модератором в runtime.
/// </summary>
public sealed class ExpirationSweeperOptions
{
    private const int DefaultSweepIntervalSeconds = 30;

    /// <summary>Пауза между проходами. По умолчанию 30 секунд.</summary>
    public int SweepIntervalSeconds { get; set; } = DefaultSweepIntervalSeconds;

    /// <summary>
    /// Автозапуск фонового сервиса. В тестовых хостах выключается
    /// (<c>Observations:SweeperEnabled=false</c>), чтобы тесты управляли проходами сами.
    /// </summary>
    public bool SweeperEnabled { get; set; } = true;

    /// <summary>Применяет значения из конфигурации процесса, если она доступна.</summary>
    public void Apply(IConfiguration? configuration)
    {
        if (configuration is null)
        {
            return;
        }

        if (int.TryParse(
                configuration["Observations:SweepIntervalSeconds"],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var intervalSeconds))
        {
            SweepIntervalSeconds = intervalSeconds;
        }

        if (bool.TryParse(configuration["Observations:SweeperEnabled"], out var enabled))
        {
            SweeperEnabled = enabled;
        }
    }

    /// <summary>Проверяет допустимость значений; вызывается при создании сервиса.</summary>
    internal void Validate()
    {
        if (SweepIntervalSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SweepIntervalSeconds),
                SweepIntervalSeconds,
                "Интервал пересчёта по TTL должен быть не меньше одной секунды.");
        }
    }
}
