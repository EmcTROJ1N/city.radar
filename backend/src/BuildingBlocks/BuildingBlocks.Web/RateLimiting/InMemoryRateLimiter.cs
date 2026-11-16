using System.Collections.Concurrent;

namespace BuildingBlocks.Web.RateLimiting;

/// <summary>
/// Скользящее окно в памяти процесса: деградация rate limit при недоступном или
/// ненастроенном Redis. Счётчики живут в одном процессе, поэтому при нескольких
/// инстансах Api лимит становится «на инстанс» — это осознанная плата за доступность.
/// </summary>
/// <remarks>
/// Для каждого ключа хранится очередь отметок времени попаданий. На каждом вызове из головы
/// удаляются попадания старше окна, поэтому окно именно скользящее, а не фиксированное.
/// Доступ к очереди — под замком ключа; сами ключи лежат в <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </remarks>
public sealed class InMemoryRateLimiter(TimeProvider time) : IRateLimiter
{
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    private int _calls;

    /// <inheritdoc />
    public Task<RateLimitResult> TryAcquireAsync(
        string key,
        int limit,
        TimeSpan window,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "Окно должно быть положительным.");
        }

        ct.ThrowIfCancellationRequested();

        var now = time.GetUtcNow();
        var bucket = _windows.GetOrAdd(key, static _ => new Window());
        RateLimitResult result;

        lock (bucket.Sync)
        {
            Trim(bucket, now, window);

            if (bucket.Hits.Count < limit)
            {
                bucket.Hits.Enqueue(now.UtcTicks);
                result = RateLimitResult.Allow(limit, bucket.Hits.Count);
            }
            else
            {
                var retryTicks = bucket.Hits.Peek() + window.Ticks - now.UtcTicks;
                result = RateLimitResult.Deny(limit, bucket.Hits.Count, TimeSpan.FromTicks(retryTicks));
            }
        }

        // Периодическая уборка пустых окон: ключи создаются на пользователей и фиксации,
        // поэтому без неё словарь рос бы вместе с числом уникальных ключей.
        if (Interlocked.Increment(ref _calls) % 1024 == 0)
        {
            Purge(now, window);
        }

        return Task.FromResult(result);
    }

    private static void Trim(Window bucket, DateTimeOffset now, TimeSpan window)
    {
        var threshold = now.UtcTicks - window.Ticks;

        while (bucket.Hits.Count > 0 && bucket.Hits.Peek() <= threshold)
        {
            bucket.Hits.Dequeue();
        }
    }

    private void Purge(DateTimeOffset now, TimeSpan window)
    {
        foreach (var (key, bucket) in _windows)
        {
            lock (bucket.Sync)
            {
                Trim(bucket, now, window);

                if (bucket.Hits.Count > 0)
                {
                    continue;
                }
            }

            _windows.TryRemove(new KeyValuePair<string, Window>(key, bucket));
        }
    }

    private sealed class Window
    {
        public object Sync { get; } = new();

        public Queue<long> Hits { get; } = new();
    }
}
