namespace IngestionWorker.Host;

/// <summary>
/// Настройки Worker-а из секции <c>Worker</c> конфигурации. Значения по умолчанию совпадают
/// с требованиями плана: опрос настроек источников раз в 10 секунд, перезапуск упавшего
/// адаптера с экспоненциальным backoff.
/// </summary>
public sealed class WorkerOptions
{
    /// <summary>Период опроса настроек источников в секундах (по умолчанию 10).</summary>
    public double ConfigPollSeconds { get; set; } = 10;

    /// <summary>Базовая задержка перезапуска упавшего адаптера в секундах (по умолчанию 1).</summary>
    public double RestartBackoffSeconds { get; set; } = 1;

    /// <summary>Верхняя граница задержки перезапуска адаптера в секундах (по умолчанию 30).</summary>
    public double RestartBackoffMaxSeconds { get; set; } = 30;

    /// <summary>Период опроса в виде интервала; неположительное значение заменяется на 10 секунд.</summary>
    public TimeSpan ConfigPollInterval =>
        TimeSpan.FromSeconds(ConfigPollSeconds > 0 ? ConfigPollSeconds : 10);

    /// <summary>Базовая задержка перезапуска; неположительное значение заменяется на секунду.</summary>
    public TimeSpan RestartBackoffBase =>
        TimeSpan.FromSeconds(RestartBackoffSeconds > 0 ? RestartBackoffSeconds : 1);

    /// <summary>Верхняя граница задержки перезапуска; не меньше базовой.</summary>
    public TimeSpan RestartBackoffMax
    {
        get
        {
            var value = RestartBackoffMaxSeconds > 0 ? RestartBackoffMaxSeconds : 30;
            var max = TimeSpan.FromSeconds(value);

            return max < RestartBackoffBase ? RestartBackoffBase : max;
        }
    }
}
