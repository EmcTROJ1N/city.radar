using Microsoft.Extensions.Configuration;

namespace BuildingBlocks.Web.RateLimiting;

/// <summary>
/// Параметры rate limit. Значения по умолчанию — стартовые лимиты из плана:
/// на пользователя 10/мин, на IP 30/мин и на фиксацию 60/мин.
/// </summary>
/// <remarks>
/// Лимит на пользователя берётся из <c>global_settings.observation_rate_per_hour</c>
/// (интерфейс <c>IdentityAccess.Contracts.IGlobalSettings</c>) с окном
/// <see cref="ObservationUserWindowSeconds"/>; значение по умолчанию из реестра настроек —
/// 20/час (<c>db/seed.sql</c>). Константы <see cref="ObservationUserFallbackLimit"/> и
/// <see cref="ObservationUserFallbackWindowSeconds"/> применяются, когда настройки прочитать
/// нельзя (сервис не зарегистрирован или чтение упало): тогда лимит — 10/мин.
/// <para>
/// Настройки читаются из секции <c>RateLimiting</c> и нужны, чтобы тесты и нагрузочные
/// стенды задавали короткие окна/малые лимиты без правки БД. Отключение
/// (<c>RateLimiting:Enabled=false</c>) применяется только в тестах, где лимиты не проверяются.
/// </para>
/// </remarks>
public sealed record RateLimitOptions
{
    /// <summary>Включён ли rate limit. По умолчанию включён.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Окно лимита на пользователя, секунды (лимит — из глобальных настроек).</summary>
    public int ObservationUserWindowSeconds { get; init; } = 3600;

    /// <summary>
    /// Лимит отметок на пользователя, когда глобальные настройки недоступны (10/мин).
    /// При доступных настройках действует <c>global_settings.observation_rate_per_hour</c>.
    /// </summary>
    public int ObservationUserFallbackLimit { get; init; } = 10;

    /// <summary>Окно резервного лимита на пользователя, секунды (1 минута).</summary>
    public int ObservationUserFallbackWindowSeconds { get; init; } = 60;

    /// <summary>Лимит отметок на IP за окно.</summary>
    public int ObservationIpLimit { get; init; } = 30;

    /// <summary>Окно лимита на IP, секунды.</summary>
    public int ObservationIpWindowSeconds { get; init; } = 60;

    /// <summary>Лимит отметок на фиксацию за окно.</summary>
    public int ObservationObjectLimit { get; init; } = 60;

    /// <summary>Окно лимита на фиксацию, секунды.</summary>
    public int ObservationObjectWindowSeconds { get; init; } = 60;

    /// <summary>Окно лимита на пользователя.</summary>
    public TimeSpan ObservationUserWindow => TimeSpan.FromSeconds(Math.Max(1, ObservationUserWindowSeconds));

    /// <summary>Окно лимита на IP.</summary>
    public TimeSpan ObservationIpWindow => TimeSpan.FromSeconds(Math.Max(1, ObservationIpWindowSeconds));

    /// <summary>Окно лимита на фиксацию.</summary>
    public TimeSpan ObservationObjectWindow => TimeSpan.FromSeconds(Math.Max(1, ObservationObjectWindowSeconds));

    /// <summary>Окно резервного лимита на пользователя (настройки недоступны).</summary>
    public TimeSpan ObservationUserFallbackWindow =>
        TimeSpan.FromSeconds(Math.Max(1, ObservationUserFallbackWindowSeconds));

    /// <summary>Читает параметры из секции <c>RateLimiting</c>; значения по умолчанию — стартовые.</summary>
    /// <param name="configuration">Конфигурация приложения.</param>
    public static RateLimitOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("RateLimiting");
        var defaults = new RateLimitOptions();

        return new RateLimitOptions
        {
            Enabled = section.GetValue("Enabled", defaults.Enabled),
            ObservationUserWindowSeconds = section.GetValue(
                "ObservationUserWindowSeconds",
                defaults.ObservationUserWindowSeconds),
            ObservationUserFallbackLimit = section.GetValue(
                "ObservationUserFallbackLimit",
                defaults.ObservationUserFallbackLimit),
            ObservationUserFallbackWindowSeconds = section.GetValue(
                "ObservationUserFallbackWindowSeconds",
                defaults.ObservationUserFallbackWindowSeconds),
            ObservationIpLimit = section.GetValue("ObservationIpLimit", defaults.ObservationIpLimit),
            ObservationIpWindowSeconds = section.GetValue(
                "ObservationIpWindowSeconds",
                defaults.ObservationIpWindowSeconds),
            ObservationObjectLimit = section.GetValue(
                "ObservationObjectLimit",
                defaults.ObservationObjectLimit),
            ObservationObjectWindowSeconds = section.GetValue(
                "ObservationObjectWindowSeconds",
                defaults.ObservationObjectWindowSeconds),
        };
    }
}
