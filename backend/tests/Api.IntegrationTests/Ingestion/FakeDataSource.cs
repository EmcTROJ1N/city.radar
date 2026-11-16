using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Ingestion.Abstractions;

namespace Api.IntegrationTests.Ingestion;

/// <summary>
/// Управляемый адаптер источника для тестов Worker-а: сообщения подаются из теста через
/// <see cref="Emit"/>, падение потока имитируется <see cref="Crash"/>. Счётчики запусков и
/// остановок позволяют проверить, что перезапускается только затронутый источник.
/// </summary>
internal sealed class FakeDataSource(Guid sourceId) : IDataSource
{
    private readonly Channel<RawMessage> _channel = Channel.CreateUnbounded<RawMessage>();
    private int _startCalls;
    private int _stopCalls;
    private int _disposeCalls;

    public Guid SourceId { get; } = sourceId;

    public int StartCalls => Volatile.Read(ref _startCalls);

    public int StopCalls => Volatile.Read(ref _stopCalls);

    public int DisposeCalls => Volatile.Read(ref _disposeCalls);

    public Task StartAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _startCalls);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        // Повторная остановка допустима: остановка после падения канала ничего не меняет.
        Interlocked.Increment(ref _stopCalls);
        _channel.Writer.TryComplete();

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<RawMessage> ReadAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var message in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return message;
        }
    }

    public Task<SourceHealth> CheckHealthAsync(CancellationToken ct) =>
        Task.FromResult(new SourceHealth(SourceState.Ok, null, null));

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCalls);

        return ValueTask.CompletedTask;
    }

    /// <summary>Подаёт сообщение в поток адаптера.</summary>
    public void Emit(RawMessage message) => _channel.Writer.TryWrite(message);

    /// <summary>Завершает поток с ошибкой — имитация падения адаптера.</summary>
    public void Crash(Exception error) => _channel.Writer.TryComplete(error);
}

/// <summary>
/// Фабрика тестовых адаптеров: хранит все созданные экземпляры, чтобы проверять перезапуски
/// и подавать сообщения в актуальный экземпляр.
/// </summary>
internal sealed class FakeAdapterFactory(string sourceType = "telegram_chat") : ISourceAdapterFactory
{
    private readonly ConcurrentDictionary<Guid, List<FakeDataSource>> _created = new();
    private readonly ConcurrentQueue<SourceRuntimeConfig> _configs = new();

    public string SourceType { get; } = sourceType;

    public IDataSource Create(SourceRuntimeConfig config)
    {
        var adapter = new FakeDataSource(config.SourceId);

        var list = _created.GetOrAdd(config.SourceId, _ => []);
        lock (list)
        {
            list.Add(adapter);
        }

        _configs.Enqueue(config);

        return adapter;
    }

    /// <summary>Сколько раз для источника создавался адаптер (1 — запуск, 2+ — перезапуски).</summary>
    public int InstanceCount(Guid sourceId) =>
        _created.TryGetValue(sourceId, out var list) ? Count(list) : 0;

    /// <summary>Последний созданный адаптер источника; <c>null</c>, если его ещё не создавали.</summary>
    public FakeDataSource? Latest(Guid sourceId)
    {
        if (!_created.TryGetValue(sourceId, out var list))
        {
            return null;
        }

        lock (list)
        {
            return list.Count == 0 ? null : list[^1];
        }
    }

    /// <summary>Все конфигурации, переданные в фабрику (для проверки секретов и прокси).</summary>
    public IReadOnlyList<SourceRuntimeConfig> Configurations => _configs.ToArray();

    private static int Count(List<FakeDataSource> list)
    {
        lock (list)
        {
            return list.Count;
        }
    }
}
