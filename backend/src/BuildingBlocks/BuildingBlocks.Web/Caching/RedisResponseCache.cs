using Microsoft.Extensions.Logging;

using StackExchange.Redis;

namespace BuildingBlocks.Web.Caching;

/// <summary>
/// Кэш готовых ответов API по строковому ключу. Абстракция нужна,
/// чтобы модуль карты не зависел от Redis напрямую и мог работать без него.
/// </summary>
public interface IResponseCache
{
    /// <summary>Читает значение; <c>null</c> — промах или кэш недоступен.</summary>
    /// <param name="key">Ключ.</param>
    /// <param name="ct">Токен отмены.</param>
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Сохраняет значение на <paramref name="ttl"/>; ошибки кэша не пробрасываются.</summary>
    /// <param name="key">Ключ.</param>
    /// <param name="value">Сериализованный ответ.</param>
    /// <param name="ttl">Срок жизни записи.</param>
    /// <param name="ct">Токен отмены.</param>
    Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default);
}

/// <summary>
/// Кэш-заглушка: используется, когда Redis не настроен. Все запросы идут в источник данных.
/// </summary>
public sealed class NullResponseCache : IResponseCache
{
    /// <inheritdoc />
    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    /// <inheritdoc />
    public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default) =>
        Task.CompletedTask;
}

/// <summary>
/// Кэш ответов в Redis. Недоступность Redis не является ошибкой запроса: чтение возвращает
/// промах, запись игнорируется, а исключения клиента не выходят наружу.
/// </summary>
/// <remarks>
/// Таймауты и «пауза после ошибки» — в <see cref="RedisConnection"/>: после первого сбоя
/// следующие запросы не ждут Redis вовсе. Значения — готовый JSON ответа, поэтому кэш
/// прозрачен для сериализации ASP.NET Core.
/// </remarks>
public sealed class RedisResponseCache(
    RedisConnection connection,
    ILogger<RedisResponseCache>? logger = null) : IResponseCache
{
    /// <inheritdoc />
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!connection.TryGet(out var redis))
        {
            return null;
        }

        try
        {
            var value = await redis
                .GetDatabase()
                .StringGetAsync(key)
                .WaitAsync(RedisConnection.OperationTimeout, ct)
                .ConfigureAwait(false);

            connection.ReportSuccess();

            return value.IsNullOrEmpty ? null : value.ToString();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            connection.ReportFailure(exception);
            logger?.LogDebug(exception, "Чтение из кэша Redis не удалось; ответ из БД");

            return null;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(
        string key,
        string value,
        TimeSpan ttl,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        if (ttl <= TimeSpan.Zero || !connection.TryGet(out var redis))
        {
            return;
        }

        try
        {
            await redis
                .GetDatabase()
                .StringSetAsync(key, value, ttl)
                .WaitAsync(RedisConnection.OperationTimeout, ct)
                .ConfigureAwait(false);

            connection.ReportSuccess();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            connection.ReportFailure(exception);
            logger?.LogDebug(exception, "Запись в кэш Redis не удалась; ответ не закэширован");
        }
    }
}
