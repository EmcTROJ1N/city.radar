using System.Text.Json;

namespace MapReadModel.Core;

/// <summary>Ответ <c>GET /map/features</c> (OpenAPI <c>MapFeaturesResponse</c>).</summary>
/// <param name="GeneratedAt">Момент формирования ответа (UTC).</param>
/// <param name="Items">Фиксации области.</param>
public sealed record MapFeaturesResponse(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<MapFeature> Items);

/// <summary>Фиксация на карте (OpenAPI <c>MapFeature</c>) с итоговым статусом.</summary>
/// <param name="Id">Идентификатор фиксации.</param>
/// <param name="Name">Название фиксации.</param>
/// <param name="CoverageKind"><c>point</c> или <c>polygon</c>.</param>
/// <param name="Geometry">GeoJSON Point/Polygon с точностью до 6 знаков.</param>
/// <param name="CategoryId">Категория (через тип объекта).</param>
/// <param name="ObjectTypeId">Тип объекта.</param>
/// <param name="GroupId">Группа.</param>
/// <param name="Address">Адрес.</param>
/// <param name="Status">
/// Итоговый статус; <c>null</c> — «неизвестно» (нет актуальных наблюдений или строки
/// проекции). Как в контракте, отсутствие статуса не скрывает фиксацию с карты.
/// </param>
public sealed record MapFeature(
    Guid Id,
    string Name,
    string CoverageKind,
    JsonElement Geometry,
    Guid? CategoryId,
    Guid? ObjectTypeId,
    Guid? GroupId,
    string? Address,
    MapStatusView? Status);

/// <summary>
/// Итоговый статус фиксации для карты (OpenAPI <c>StatusView</c>): код, подпись, цвет,
/// иконка, свежесть, время и тип источника.
/// </summary>
/// <param name="SourceStatusId">Статус источника.</param>
/// <param name="Code">Код статуса.</param>
/// <param name="Name">Название статуса.</param>
/// <param name="Color">Ключ палитры оформления.</param>
/// <param name="Icon">Иконка статуса.</param>
/// <param name="Freshness"><c>fresh</c>, <c>aging</c> или <c>stale</c>.</param>
/// <param name="ObservedAt">Время наблюдения-победителя.</param>
/// <param name="SourceType"><c>manual</c>, <c>moderator</c>, <c>telegram</c> или <c>max</c>.</param>
/// <param name="ObservationCount">Количество актуальных наблюдений.</param>
public sealed record MapStatusView(
    Guid SourceStatusId,
    string Code,
    string Name,
    string Color,
    string Icon,
    string Freshness,
    DateTimeOffset ObservedAt,
    string SourceType,
    int ObservationCount);

/// <summary>
/// Сборка ответа карты. Отдельный сервис — точка расширения для ETag/кэша (задачи 5.2, 5.4):
/// они оборачивают <see cref="GetAsync"/>, не меняя выборку.
/// </summary>
public class MapFeaturesService(MapQueries queries, TimeProvider time)
{
    /// <summary>Читает фиксации области и формирует ответ по контракту.</summary>
    /// <param name="query">Проверенные параметры запроса.</param>
    /// <param name="ct">Токен отмены.</param>
    public virtual async Task<MapFeaturesResponse> GetAsync(
        MapFeaturesQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var rows = await queries.GetFeaturesAsync(query, ct);
        var items = new MapFeature[rows.Count];

        for (var index = 0; index < rows.Count; index++)
        {
            items[index] = ToFeature(rows[index]);
        }

        return new MapFeaturesResponse(time.GetUtcNow(), items);
    }

    private static MapFeature ToFeature(MapFeatureRow row) => new(
        row.Id,
        row.Name,
        row.CoverageKind,
        row.Geometry,
        row.CategoryId,
        row.ObjectTypeId,
        row.GroupId,
        row.Address,
        ToStatus(row));

    private static MapStatusView? ToStatus(MapFeatureRow row)
    {
        // Строка проекции с source_status_id = null означает «неизвестно»; в контракте
        // это status: null, а не статус с пустыми полями.
        if (row.SourceStatusId is not { } sourceStatusId
            || row.StatusCode is null
            || row.StatusName is null
            || row.StatusColor is null
            || row.StatusIcon is null
            || row.Freshness is null
            || row.ObservedAt is not { } observedAt
            || row.SourceType is null)
        {
            return null;
        }

        return new MapStatusView(
            sourceStatusId,
            row.StatusCode,
            row.StatusName,
            row.StatusColor,
            row.StatusIcon,
            row.Freshness,
            observedAt,
            row.SourceType,
            row.ObservationCount);
    }
}
