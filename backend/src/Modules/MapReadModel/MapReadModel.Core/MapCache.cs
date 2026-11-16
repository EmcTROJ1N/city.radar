using System.Text.Json;

using BuildingBlocks.Web.Caching;

namespace MapReadModel.Core;

/// <summary>
/// Кэш ответа <c>GET /map/features</c>: декоратор вокруг
/// <see cref="MapFeaturesService"/>, который не меняет выборку, а переиспользует готовый ответ.
/// </summary>
/// <remarks>
/// Ключ — <c>map:{version}:{normalizedQuery}</c>, где <c>version</c> — версия состояния карты
/// (<see cref="MapVersionProvider"/>). Любая правка фиксаций, статусов или каталога меняет
/// версию, поэтому инвалидация не требует явных удалений: старые ключи доживают TTL и
/// перестают запрашиваться. TTL в <see cref="Ttl"/> страхует от «залипания» данных, если
/// версия почему-то не изменилась.
/// <para>
/// 304 по ETag отдаётся в <c>MapEndpoints</c> до вызова этого класса: условный запрос не
/// трогает ни Redis, ни БД. Сериализуется ровно то представление, которое уходит клиенту
/// (camelCase, те же поля), поэтому попадание в кэш неотличимо от ответа из БД.
/// </para>
/// <para>
/// При недоступном Redis <see cref="IResponseCache"/> возвращает промах и игнорирует запись:
/// карта отвечает из БД и не падает с 500.
/// </para>
/// </remarks>
public sealed class MapCache(MapFeaturesService features, IResponseCache cache)
{
    /// <summary>Срок жизни записи кэша ответа карты.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Возвращает ответ карты: из кэша при попадании, иначе из <see cref="MapFeaturesService"/>
    /// с сохранением результата в кэш.
    /// </summary>
    /// <param name="query">Проверенные параметры запроса.</param>
    /// <param name="version">Версия состояния карты (уже прочитанная для ETag).</param>
    /// <param name="ct">Токен отмены.</param>
    public async Task<MapFeaturesResponse> GetAsync(
        MapFeaturesQuery query,
        string version,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var key = BuildKey(version, query);
        var cached = await cache.GetAsync(key, ct).ConfigureAwait(false);

        if (cached is not null && TryDeserialize(cached, out var hit))
        {
            return hit;
        }

        var response = await features.GetAsync(query, ct).ConfigureAwait(false);
        var payload = JsonSerializer.Serialize(response, JsonOptions);

        await cache.SetAsync(key, payload, Ttl, ct).ConfigureAwait(false);

        return response;
    }

    /// <summary>Ключ кэша: версия состояния карты и канонические параметры запроса.</summary>
    /// <param name="version">Версия состояния карты.</param>
    /// <param name="query">Параметры запроса.</param>
    public static string BuildKey(string version, MapFeaturesQuery query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(query);

        return $"map:{version}:{query.ToNormalizedString()}";
    }

    private static bool TryDeserialize(string payload, out MapFeaturesResponse response)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<MapFeaturesResponse>(payload, JsonOptions);

            if (parsed is not null)
            {
                response = parsed;

                return true;
            }
        }
        catch (JsonException)
        {
            // Повреждённая/устаревшая запись кэша — не ошибка запроса: соберём ответ из БД.
        }

        response = null!;

        return false;
    }
}
