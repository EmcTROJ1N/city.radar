using Microsoft.Extensions.Logging;

namespace Api.IntegrationTests.Ingestion;

/// <summary>
/// Управляемое время для проверки TTL кэша настроек LLM: тест двигает часы вперёд,
/// не дожидаясь реальных 30 секунд.
/// </summary>
internal sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Сдвигает текущее время вперёд.</summary>
    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}

/// <summary>
/// Логгер, запоминающий отформатированные сообщения: используется для проверки, что ключ LLM
/// и тело сообщения не попадают в журнал.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly object _sync = new();
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_sync)
            {
                return _messages.ToArray();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        if (exception is not null)
        {
            message = $"{message} | {exception.GetType().Name}: {exception.Message}";
        }

        lock (_sync)
        {
            _messages.Add(message);
        }
    }
}
