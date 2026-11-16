namespace BuildingBlocks.Web.RateLimiting;

/// <summary>
/// Результат попытки получить «слот» в скользящем окне.
/// </summary>
/// <param name="Allowed">Запрос разрешён.</param>
/// <param name="Limit">Лимит окна.</param>
/// <param name="Count">Число попаданий в окне (после текущего, если запрос разрешён).</param>
/// <param name="RetryAfter">Через сколько можно повторить; <see cref="TimeSpan.Zero"/> при разрешении.</param>
public readonly record struct RateLimitResult(bool Allowed, int Limit, int Count, TimeSpan RetryAfter)
{
    /// <summary>Значение заголовка <c>Retry-After</c>: целые секунды, минимум одна.</summary>
    public int RetryAfterSeconds => RetryAfter <= TimeSpan.Zero
        ? 0
        : Math.Max(1, (int)Math.Ceiling(RetryAfter.TotalSeconds));

    /// <summary>Разрешённый запрос.</summary>
    public static RateLimitResult Allow(int limit, int count) => new(true, limit, count, TimeSpan.Zero);

    /// <summary>Отклонённый запрос с временем до освобождения слота.</summary>
    public static RateLimitResult Deny(int limit, int count, TimeSpan retryAfter) =>
        new(false, limit, count, retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter);
}

/// <summary>
/// Скользящее окно запросов по ключу (: «на пользователя, IP и объект»).
/// Реализации: Redis (общая для инстансов Api) и память процесса (деградация без Redis).
/// Интерфейс общий, чтобы лимиты могли применяться к разным эндпоинтам (отметки, жалобы).
/// </summary>
public interface IRateLimiter
{
    /// <summary>
    /// Учитывает одну попытку по ключу и сообщает, разрешена ли она.
    /// </summary>
    /// <param name="key">Ключ окна, например <c>rl:obs:user:{id}</c>.</param>
    /// <param name="limit">Максимум попыток в окне (≥ 1).</param>
    /// <param name="window">Длина скользящего окна (&gt; 0).</param>
    /// <param name="ct">Токен отмены.</param>
    Task<RateLimitResult> TryAcquireAsync(
        string key,
        int limit,
        TimeSpan window,
        CancellationToken ct = default);
}
