using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

using BuildingBlocks.Web.Caching;

namespace MapReadModel.Core.Geocoding;

/// <summary>
/// Геокодер на OSM Nominatim. Вызывается только с бэкенда: не чаще одного запроса в секунду на
/// процесс, найденные адреса кэшируются на 30 дней, не найденные — на день (в памяти процесса и в
/// <see cref="IResponseCache"/>, чтобы без Redis не повторять запросы на каждом опросе карты).
/// Ошибки сети и 5xx не кэшируются, но после них сеть не трогается
/// <see cref="GeocodingOptions.FailurePause"/>: лежащий Nominatim не держит каждый опрос карты.
/// </summary>
public sealed class NominatimGeocoder : IGeocoder, IDisposable
{
    private static readonly TimeSpan FoundTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan NotFoundTtl = TimeSpan.FromDays(1);
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);
    private const double HintMarginDegrees = 0.01;
    private const int MemoryCacheLimit = 10_000;
    private const string NotFoundValue = "-";

    private readonly GeocodingOptions _options;
    private readonly IResponseCache _cache;
    private readonly TimeProvider _time;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (string Value, DateTimeOffset ExpiresAt)> _memory = new();
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

    /// <param name="options">Настройки геокодера.</param>
    /// <param name="cache">Общий кэш (Redis или пустой).</param>
    /// <param name="time">Часы: TTL кэша в памяти и интервал между запросами.</param>
    /// <param name="handler">Обработчик HTTP; задаётся в тестах.</param>
    public NominatimGeocoder(
        GeocodingOptions options,
        IResponseCache cache,
        TimeProvider time,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(time);

        _options = options;
        _cache = cache;
        _time = time;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(5);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
    }

    /// <inheritdoc />
    public async Task<GeoPoint?> GeocodeAsync(string address, GeoBox hint, GeocodeBudget? budget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hint);
        if (!_options.Enabled || string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var key = "geocode:v1:" + AddressNormalizer.Normalize(address);
        if (await ReadCachedAsync(key, ct) is { } cached)
        {
            return Parse(cached);
        }

        if (_time.GetUtcNow() < _pausedUntil || (budget is not null && !budget.TryTake()))
        {
            return null;
        }

        var value = await RequestAsync(address, hint, ct);
        if (value is null)
        {
            return null;
        }

        await WriteCachedAsync(key, value, value == NotFoundValue ? NotFoundTtl : FoundTtl, ct);

        return Parse(value);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    private async Task<string?> ReadCachedAsync(string key, CancellationToken ct)
    {
        if (_memory.TryGetValue(key, out var entry) && entry.ExpiresAt > _time.GetUtcNow())
        {
            return entry.Value;
        }

        return await _cache.GetAsync(key, ct);
    }

    private async Task WriteCachedAsync(string key, string value, TimeSpan ttl, CancellationToken ct)
    {
        if (_memory.Count >= MemoryCacheLimit)
        {
            _memory.Clear();
        }

        _memory[key] = (value, _time.GetUtcNow() + ttl);
        await _cache.SetAsync(key, value, ttl, ct);
    }

    /// <summary>Запрос к Nominatim: <c>"lat,lon"</c>, <see cref="NotFoundValue"/> или <c>null</c> при ошибке.</summary>
    private async Task<string?> RequestAsync(string address, GeoBox hint, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _lastRequestAt + MinInterval - _time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _time, ct);
            }

            _lastRequestAt = _time.GetUtcNow();

            using var response = await _http.GetAsync(BuildUri(address, hint), ct);
            if (!response.IsSuccessStatusCode)
            {
                Pause();
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            return ReadFirst(document.RootElement) ?? NotFoundValue;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException
                                          && !ct.IsCancellationRequested)
        {
            Pause();
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Pause() => _pausedUntil = _time.GetUtcNow() + _options.FailurePause;

    private Uri BuildUri(string address, GeoBox hint)
    {
        var viewbox = string.Join(
            ',',
            new[]
            {
                hint.MinLon - HintMarginDegrees,
                hint.MaxLat + HintMarginDegrees,
                hint.MaxLon + HintMarginDegrees,
                hint.MinLat - HintMarginDegrees,
            }.Select(value => value.ToString("0.######", CultureInfo.InvariantCulture)));
        var query = Uri.EscapeDataString($"{address}, {_options.City}");

        return new Uri(
            _options.NominatimUrl,
            $"search?q={query}&format=jsonv2&limit=1&accept-language=ru&viewbox={viewbox}&bounded=1");
    }

    private static string? ReadFirst(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            return null;
        }

        var first = root[0];
        if (!first.TryGetProperty("lat", out var lat) || !first.TryGetProperty("lon", out var lon) ||
            !double.TryParse(lat.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var latValue) ||
            !double.TryParse(lon.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lonValue))
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{latValue},{lonValue}");
    }

    private static GeoPoint? Parse(string value)
    {
        if (value == NotFoundValue)
        {
            return null;
        }

        var parts = value.Split(',');
        return parts.Length == 2 &&
               double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) &&
               double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
            ? new GeoPoint(lat, lon)
            : null;
    }
}
