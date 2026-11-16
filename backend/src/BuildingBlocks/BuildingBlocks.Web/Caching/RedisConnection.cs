using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using StackExchange.Redis;

namespace BuildingBlocks.Web.Caching;

/// <summary>
/// Подключение к Redis с короткими таймаутами и «паузой после ошибки».
/// </summary>
/// <remarks>
/// Redis в продукте — ускоритель, а не источник правды: его недоступность не должна ни
/// ронять запрос, ни добавлять к нему секунды ожидания. Поэтому:
/// <list type="bullet">
///   <item><description>подключение создаётся с <c>ConnectTimeout</c>/<c>AsyncTimeout</c> не
///   более <see cref="OperationTimeout"/> и <c>AbortOnConnectFail = false</c> — приложение
///   стартует, даже если Redis недоступен;</description></item>
///   <item><description>каждая операция ограничена <see cref="OperationTimeout"/>: после
///   первой ошибки обмен с Redis приостанавливается на <see cref="PauseAfterFailure"/>, и
///   следующие запросы сразу идут в деградированный режим, не дожидаясь таймаута;</description></item>
///   <item><description>пустая строка подключения (<c>ConnectionStrings:Redis</c>) означает
///   «Redis не настроен»: потребители работают без него и не пытаются подключаться.</description></item>
/// </list>
/// Объект один на процесс (singleton), чтобы circuit breaker был общим для кэша карты и
/// лимитера запросов.
/// </remarks>
public sealed class RedisConnection : IDisposable
{
    /// <summary>Максимальное время одной операции с Redis (connect и команда), мс.</summary>
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>Пауза после ошибки Redis, в течение которой операции не выполняются.</summary>
    public static readonly TimeSpan PauseAfterFailure = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time;
    private readonly ILogger<RedisConnection>? _logger;
    private readonly IConnectionMultiplexer? _multiplexer;
    private readonly object _sync = new();

    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

    /// <summary>Создаёт подключение по строке <c>ConnectionStrings:Redis</c>.</summary>
    /// <param name="configuration">Конфигурация приложения.</param>
    /// <param name="time">Источник времени для паузы после ошибки.</param>
    /// <param name="logger">Журнал: ошибки Redis пишутся предупреждением, а не исключением.</param>
    public RedisConnection(
        IConfiguration configuration,
        TimeProvider time,
        ILogger<RedisConnection>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(time);

        _time = time;
        _logger = logger;

        var connectionString = configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        IsConfigured = true;
        _multiplexer = TryConnect(connectionString, logger);
    }

    /// <summary>Задана ли строка подключения к Redis.</summary>
    public bool IsConfigured { get; }

    /// <summary>
    /// Возвращает подключение, если Redis настроен, доступен и не находится в паузе после
    /// ошибки. <c>false</c> — вызывающий код должен деградировать (БД/память).
    /// </summary>
    /// <param name="connection">Подключение для операции.</param>
    public bool TryGet(out IConnectionMultiplexer connection)
    {
        connection = null!;

        if (_multiplexer is not { } multiplexer)
        {
            return false;
        }

        lock (_sync)
        {
            if (_time.GetUtcNow() < _pausedUntil)
            {
                return false;
            }
        }

        connection = multiplexer;

        return true;
    }

    /// <summary>Отмечает успешный обмен: снимает паузу после прошлой ошибки.</summary>
    public void ReportSuccess()
    {
        lock (_sync)
        {
            _pausedUntil = DateTimeOffset.MinValue;
        }
    }

    /// <summary>Отмечает ошибку: операции с Redis приостанавливаются на <see cref="PauseAfterFailure"/>.</summary>
    /// <param name="exception">Исключение клиента Redis.</param>
    public void ReportFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        lock (_sync)
        {
            _pausedUntil = _time.GetUtcNow() + PauseAfterFailure;
        }

        _logger?.LogWarning(
            exception,
            "Redis недоступен; {Seconds} с работаем без него",
            PauseAfterFailure.TotalSeconds);
    }

    /// <inheritdoc />
    public void Dispose() => _multiplexer?.Dispose();

    private static IConnectionMultiplexer? TryConnect(
        string connectionString,
        ILogger<RedisConnection>? logger)
    {
        try
        {
            var options = ConfigurationOptions.Parse(connectionString);
            options.AbortOnConnectFail = false;
            options.ConnectTimeout = (int)OperationTimeout.TotalMilliseconds;
            options.AsyncTimeout = (int)OperationTimeout.TotalMilliseconds;
            options.SyncTimeout = (int)OperationTimeout.TotalMilliseconds;
            options.ConnectRetry = 1;

            return ConnectionMultiplexer.Connect(options);
        }
        catch (Exception exception)
        {
            // Битый адрес/неразрешимое имя: приложение обязано подняться без Redis.
            logger?.LogWarning(exception, "Не удалось создать подключение к Redis");

            return null;
        }
    }
}
