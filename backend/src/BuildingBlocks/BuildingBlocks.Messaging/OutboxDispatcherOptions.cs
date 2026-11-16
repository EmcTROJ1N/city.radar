namespace BuildingBlocks.Messaging;

/// <summary>
/// Настройки <see cref="OutboxDispatcher"/> для одного destination.
/// </summary>
public sealed class OutboxDispatcherOptions
{
    /// <summary>Сторона-получатель, которую обслуживает диспетчер: <c>api</c> или <c>worker</c>.</summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>Пауза между опросами таблицы, когда очередь пуста. По умолчанию 1 секунда.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Сколько сообщений захватывается за один опрос. По умолчанию 20.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>
    /// После какого числа неудачных попыток сообщение перестаёт выбираться. Остаётся
    /// необработанным с <c>last_error</c> и не блокирует остальные. По умолчанию 10.
    /// </summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>Верхняя граница экспоненциальной задержки повтора. По умолчанию 5 минут.</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Проверяет допустимость значений; вызывается при создании диспетчера.</summary>
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Destination))
        {
            throw new ArgumentException("Не задан destination диспетчера outbox.", nameof(Destination));
        }

        if (PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PollInterval),
                PollInterval,
                "Интервал опроса outbox должен быть положительным.");
        }

        if (BatchSize < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BatchSize),
                BatchSize,
                "Размер пачки outbox должен быть не меньше единицы.");
        }

        if (MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxAttempts),
                MaxAttempts,
                "Предел попыток outbox должен быть не меньше единицы.");
        }

        if (MaxBackoff <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxBackoff),
                MaxBackoff,
                "Максимальная задержка повтора outbox должна быть положительной.");
        }
    }
}
