namespace MapReadModel.Core.Geocoding;

/// <summary>Координаты точки, WGS 84.</summary>
/// <param name="Lat">Широта.</param>
/// <param name="Lon">Долгота.</param>
public sealed record GeoPoint(double Lat, double Lon);

/// <summary>Прямоугольник поиска, WGS 84.</summary>
public sealed record GeoBox(double MinLon, double MinLat, double MaxLon, double MaxLat);

/// <summary>
/// Геокодирование адреса из сообщения в координаты. От геокодера берутся только координаты:
/// названия из OSM в интерфейс не попадают (C-01).
/// </summary>
public interface IGeocoder
{
    /// <summary>Координаты адреса рядом с <paramref name="hint"/>; <c>null</c> — не найден или геокодер недоступен.</summary>
    /// <param name="address">Адрес «улица дом» из сообщения.</param>
    /// <param name="hint">Область поиска (габарит зоны).</param>
    /// <param name="budget">
    /// Сколько запросов к сети разрешено вызывающему; ответ из кэша бюджет не тратит.
    /// Бюджет исчерпан — <c>null</c> без запроса. <c>null</c> — без ограничения.
    /// </param>
    /// <param name="ct">Токен отмены.</param>
    Task<GeoPoint?> GeocodeAsync(string address, GeoBox hint, GeocodeBudget? budget, CancellationToken ct);
}

/// <summary>Лимит запросов к геокодеру на один вызов эндпоинта: не потокобезопасен, живёт один запрос.</summary>
/// <param name="requests">Сколько запросов к сети разрешено.</param>
public sealed class GeocodeBudget(int requests)
{
    /// <summary>Сколько запросов осталось.</summary>
    public int Remaining { get; private set; } = requests;

    /// <summary>Берёт один запрос из бюджета; <c>false</c>, если он исчерпан.</summary>
    public bool TryTake()
    {
        if (Remaining <= 0)
        {
            return false;
        }

        Remaining--;
        return true;
    }
}
