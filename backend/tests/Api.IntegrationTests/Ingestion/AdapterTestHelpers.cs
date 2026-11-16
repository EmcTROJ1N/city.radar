using System.Net;
using System.Text;
using System.Text.Json;

using Ingestion.Abstractions;
using Ingestion.Adapters.TelegramMtProto;

namespace Api.IntegrationTests.Ingestion;

/// <summary>Одно сообщение о health, полученное подменённым <c>ISourceHealthReporter</c>.</summary>
internal sealed record HealthReport(
    SourceState State,
    string? Detail,
    DateTimeOffset? LastMessageAt);

/// <summary>
/// Запись health-сообщений адаптера без БД: адаптеры получают колбэк из
/// <see cref="SourceRuntimeConfig.ReportHealth"/>, тест проверяет последовательность состояний.
/// </summary>
internal sealed class HealthRecorder
{
    private readonly object _sync = new();
    private readonly List<HealthReport> _reports = [];

    public SourceHealthCallback Callback => Report;

    public IReadOnlyList<HealthReport> Reports
    {
        get
        {
            lock (_sync)
            {
                return _reports.ToArray();
            }
        }
    }

    public bool Has(SourceState state) => Reports.Any(report => report.State == state);

    public HealthReport? Last(SourceState state) =>
        Reports.LastOrDefault(report => report.State == state);

    private Task Report(
        SourceState state,
        string? detail,
        DateTimeOffset? lastMessageAt,
        CancellationToken ct)
    {
        lock (_sync)
        {
            _reports.Add(new HealthReport(state, detail, lastMessageAt));
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Управляемое время для таймеров адаптеров: <see cref="Advance"/> сдвигает часы и запускает
/// сработавшие <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>.
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);

        lock (_sync)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>Сдвигает часы и синхронно запускает все таймеры, срок которых наступил.</summary>
    public void Advance(TimeSpan delta)
    {
        lock (_sync)
        {
            _now = _now.Add(delta);
        }

        for (var guard = 0; guard < 1000; guard++)
        {
            ManualTimer? due;
            lock (_sync)
            {
                due = _timers
                    .Where(timer => timer.DueAt <= _now)
                    .OrderBy(timer => timer.DueAt)
                    .FirstOrDefault();
            }

            if (due is null)
            {
                return;
            }

            due.Fire();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_sync)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(
        ManualTimeProvider owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        private DateTimeOffset? _dueAt;
        private bool _disposed;

        public DateTimeOffset DueAt => _dueAt ?? DateTimeOffset.MaxValue;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._sync)
            {
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            }

            return true;
        }

        public void Fire()
        {
            lock (owner._sync)
            {
                if (_disposed || _dueAt is null || _dueAt > owner._now)
                {
                    return;
                }

                _dueAt = null;
                owner._timers.Remove(this);
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (owner._sync)
            {
                _disposed = true;
            }

            owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Подменённый HTTP-транспорт Telegram Bot API: отдаёт заранее заданные ответы и запоминает
/// запросы. Реальная сеть не используется.
/// </summary>
internal sealed class BotApiHandler(
    Func<int, string> responseFactory,
    Func<int, HttpStatusCode>? statusFactory = null) : HttpMessageHandler
{
    private readonly object _sync = new();
    private readonly List<string> _requests = [];
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_sync)
            {
                return _requests.ToArray();
            }
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _calls);

        lock (_sync)
        {
            _requests.Add(request.RequestUri!.PathAndQuery);
        }

        var response = new HttpResponseMessage(statusFactory?.Invoke(call) ?? HttpStatusCode.OK)
        {
            Content = new StringContent(responseFactory(call), Encoding.UTF8, "application/json"),
        };

        return Task.FromResult(response);
    }
}

/// <summary>Транспорт MTProto с заданными действиями: подмена WTelegramClient в тестах.</summary>
internal sealed class StubTelegramTransport(
    Func<Task>? open = null,
    Func<CancellationToken, IAsyncEnumerable<MtProtoIncomingMessage>>? read = null)
    : ITelegramClientTransport
{
    private readonly Func<Task>? _open = open;
    private readonly Func<CancellationToken, IAsyncEnumerable<MtProtoIncomingMessage>>? _read = read;

    public Task OpenAsync(MtProtoTransportConfig config, string chatId, CancellationToken ct) =>
        _open?.Invoke() ?? Task.CompletedTask;

    public IAsyncEnumerable<MtProtoIncomingMessage> ReadAsync(CancellationToken ct) =>
        _read?.Invoke(ct) ?? Empty(ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async IAsyncEnumerable<MtProtoIncomingMessage> Empty(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }
}

/// <summary>Фабрика транспортов, выдающая сценарий по номеру подключения.</summary>
internal sealed class ScriptedTransportFactory(
    Func<int, ITelegramClientTransport> factory) : ITelegramClientTransportFactory
{
    private int _created;

    public int CreatedCount => Volatile.Read(ref _created);

    public ITelegramClientTransport Create() => factory(Interlocked.Increment(ref _created));
}

/// <summary>Сценарии потока сообщений MTProto для тестов.</summary>
internal static class TelegramTransportScripts
{
    /// <summary>Отдаёт сообщения и держит поток открытым до отмены.</summary>
    public static async IAsyncEnumerable<MtProtoIncomingMessage> YieldThenBlock(
        IReadOnlyList<MtProtoIncomingMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();

            yield return message;
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    /// <summary>Сразу завершает подключение с отказом MTProto.</summary>
    public static Func<Task> Fail(TelegramTransportException exception) =>
        async () =>
        {
            await Task.Yield();
            throw exception;
        };
}

/// <summary>Создание конфигураций и JSON-ответов Bot API для тестов адаптеров.</summary>
internal static class AdapterTestData
{
    public const string ChatUsername = "water_chat";
    public const string ChatId = "@water_chat";
    public const string BotToken = "123456:TEST-TOKEN";

    public static SourceRuntimeConfig Config(
        string sourceType = TelegramSourceSettings.SourceType,
        string? mode = null,
        string chatId = ChatId,
        string? session = null,
        ProxySettings? proxy = null,
        bool proxyRequired = false,
        SourceHealthCallback? reportHealth = null)
    {
        var settings = mode is null
            ? JsonSerializer.Serialize(new { chatId })
            : JsonSerializer.Serialize(new { chatId, mode });

        using var document = JsonDocument.Parse(settings);

        return new SourceRuntimeConfig(
            Guid.NewGuid(),
            sourceType,
            document.RootElement.Clone(),
            session ?? MtProtoSession(),
            proxy,
            proxyRequired,
            reportHealth);
    }

    public static string MtProtoSession(
        int apiId = 12345,
        string apiHash = "0123456789abcdef0123456789abcdef") =>
        JsonSerializer.Serialize(new
        {
            apiId,
            apiHash,
            sessionData = "U0VTU0lPTg==",
            phone = "+70000000000",
        });

    public static string GetUpdates(params string[] messages)
    {
        var updates = messages
            .Select(
                (message, index) =>
                    $$"""{"update_id":{{(index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}},"message":{{message}}}""");

        return $$"""{"ok":true,"result":[{{string.Join(',', updates)}}]}""";
    }

    public static string TelegramMessage(
        long messageId,
        string text,
        long chatId = 777,
        string? username = ChatUsername,
        bool fromBot = false,
        string? title = "Вода")
    {
        var usernameJson = username is null ? "null" : JsonSerializer.Serialize(username);
        var author = fromBot
            ? """{"id":42,"is_bot":true,"username":"water_bot"}"""
            : """{"id":7,"is_bot":false,"username":"житель"}""";

        return $$"""
        {
          "message_id": {{messageId.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
          "date": 1763200000,
          "chat": {"id": {{chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "type": "supergroup", "title": {{JsonSerializer.Serialize(title)}}, "username": {{usernameJson}}},
          "from": {{author}},
          "text": {{JsonSerializer.Serialize(text)}}
        }
        """;
    }

    /// <summary>Свободный локальный порт, на котором ничего не слушает (недоступный прокси).</summary>
    public static int UnusedPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        listener.Stop();

        return port;
    }
}
