using Microsoft.Extensions.Configuration;

namespace MapReadModel.Core.Geocoding;

/// <summary>Настройки геокодера из секции <c>Geocoding</c>.</summary>
public sealed record GeocodingOptions
{
    /// <summary>Базовый адрес Nominatim.</summary>
    public Uri NominatimUrl { get; init; } = new("https://nominatim.openstreetmap.org");

    /// <summary>User-Agent запросов: политика публичного Nominatim требует осмысленный.</summary>
    public string UserAgent { get; init; } = "mayak-water/1.0";

    /// <summary>Город, который дописывается к адресу из сообщения.</summary>
    public string City { get; init; } = "Донецк";

    /// <summary>Пауза запросов к сети после ошибки или таймаута: не долбим лежащий Nominatim.</summary>
    public TimeSpan FailurePause { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Выключенный геокодер ничего не запрашивает и не находит.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Читает параметры из секции <c>Geocoding</c>; отсутствующие — значения по умолчанию.</summary>
    /// <param name="configuration">Конфигурация приложения.</param>
    public static GeocodingOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("Geocoding");
        var defaults = new GeocodingOptions();
        var url = section["NominatimUrl"];

        return new GeocodingOptions
        {
            NominatimUrl = string.IsNullOrWhiteSpace(url) ? defaults.NominatimUrl : new Uri(url),
            UserAgent = section["UserAgent"] is { Length: > 0 } agent ? agent : defaults.UserAgent,
            City = section["City"] is { Length: > 0 } city ? city : defaults.City,
            Enabled = section.GetValue("Enabled", defaults.Enabled),
        };
    }
}
