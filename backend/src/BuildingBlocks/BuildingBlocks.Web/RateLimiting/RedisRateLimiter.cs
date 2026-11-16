using BuildingBlocks.Web.Caching;

using Microsoft.Extensions.Logging;

using StackExchange.Redis;

namespace BuildingBlocks.Web.RateLimiting;

/// <summary>
/// Rate limit со скользящим окном в Redis.
/// </summary>
/// <remarks>
/// Окно — сортированное множество попаданий на ключ: Lua-скрипт атомарно удаляет попадания
/// старше окна, считает оставшиеся и либо добавляет текущее, либо возвращает время до
/// освобождения слота. Один скрипт вместо «прочитать — посчитать — записать» исключает
/// гонку между параллельными запросами и несколькими инстансами Api.
/// <para>
/// Если Redis не настроен, недоступен или находится в паузе после ошибки, лимит считает
/// <see cref="InMemoryRateLimiter"/>: запрос отвечает 429 по лимиту процесса, а не падает
/// с 500. Возврат к Redis происходит автоматически после паузы.
/// </para>
/// </remarks>
public sealed class RedisRateLimiter(
    RedisConnection connection,
    InMemoryRateLimiter fallback,
    TimeProvider time,
    ILogger<RedisRateLimiter>? logger = null) : IRateLimiter
{
    /// <summary>
    /// Атомарное скользящее окно: <c>KEYS[1]</c> — ключ, <c>ARGV</c> — сейчас (мс), окно (мс),
    /// лимит и уникальный член множества. Возвращает <c>{allowed, count, retryAfterMs}</c>.
    /// </summary>
    private const string SlidingWindowScript = """
        local key = KEYS[1]
        local now = tonumber(ARGV[1])
        local window = tonumber(ARGV[2])
        local limit = tonumber(ARGV[3])
        local member = ARGV[4]

        redis.call('ZREMRANGEBYSCORE', key, '-inf', now - window)
        local count = redis.call('ZCARD', key)

        if count < limit then
            redis.call('ZADD', key, now, member)
            redis.call('PEXPIRE', key, window)
            return {1, count + 1, 0}
        end

        local oldest = redis.call('ZRANGE', key, 0, 0, 'WITHSCORES')
        local retry = 0
        if #oldest >= 2 then
            retry = tonumber(oldest[2]) + window - now
        end

        if retry < 0 then
            retry = 0
        end

        return {0, count, math.floor(retry)}
        """;

    /// <inheritdoc />
    public async Task<RateLimitResult> TryAcquireAsync(
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

        var now = time.GetUtcNow();

        if (!connection.TryGet(out var redis))
        {
            return await fallback.TryAcquireAsync(key, limit, window, ct).ConfigureAwait(false);
        }

        try
        {
            var reply = await redis
                .GetDatabase()
                .ScriptEvaluateAsync(
                    SlidingWindowScript,
                    [key],
                    [
                        now.ToUnixTimeMilliseconds(),
                        (long)window.TotalMilliseconds,
                        limit,
                        Guid.NewGuid().ToString("N"),
                    ])
                .WaitAsync(RedisConnection.OperationTimeout, ct)
                .ConfigureAwait(false);

            connection.ReportSuccess();

            var values = (RedisResult[]?)reply;
            if (values is null || values.Length < 3)
            {
                throw new InvalidOperationException("Скрипт rate limit вернул неожиданный ответ.");
            }

            var allowed = (long)values[0] == 1;
            var count = (int)(long)values[1];
            var retryAfter = TimeSpan.FromMilliseconds(Math.Max(0, (long)values[2]));

            return allowed
                ? RateLimitResult.Allow(limit, count)
                : RateLimitResult.Deny(limit, count, retryAfter);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            connection.ReportFailure(exception);
            logger?.LogDebug(exception, "Rate limit в Redis недоступен; лимит по памяти процесса");

            return await fallback.TryAcquireAsync(key, limit, window, ct).ConfigureAwait(false);
        }
    }
}
