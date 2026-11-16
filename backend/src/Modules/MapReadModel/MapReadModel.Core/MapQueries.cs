using System.Data;
using System.Data.Common;
using System.Text.Json;

using BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;

namespace MapReadModel.Core;

/// <summary>
/// Чтение карты по bbox: сырой SQL по <c>map_objects</c> и
/// проекции <c>current_statuses</c>. Модуль ничего не пишет и не трогает доменные сущности
/// каталога/наблюдений — только read-only проекция.
/// </summary>
/// <remarks>
/// Пространственный отбор идёт по GiST-индексу <c>map_objects_geometry_gist</c>:
/// <c>&amp;&amp;</c> отсекает по ограничивающему прямоугольнику, <c>ST_Intersects</c> уточняет
/// пересечение (полигон, пересекающий bbox без вершин внутри, тоже попадает в выдачу).
/// Полигоны упрощаются <c>ST_Simplify</c> с допуском по zoom, точки не упрощаются.
/// </remarks>
public sealed class MapQueries(MayakDbContext db)
{
    /// <summary>
    /// Верхняя граница числа объектов в ответе. Контракт не описывает признак переполнения,
    /// поэтому выдача детерминированно обрезается по <c>id</c>: клиент видит первые
    /// <see cref="MaxFeatures"/> фиксаций области и сужает bbox/фильтры.
    /// </summary>
    public const int MaxFeatures = 5000;

    /// <summary>Загружает фиксации области с итоговым статусом.</summary>
    /// <param name="query">Проверенные параметры запроса.</param>
    /// <param name="ct">Токен отмены.</param>
    public async Task<IReadOnlyList<MapFeatureRow>> GetFeaturesAsync(
        MapFeaturesQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var sql = BuildSql(query);
        var connection = db.Database.GetDbConnection();
        var openedHere = false;

        try
        {
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(ct);
                openedHere = true;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameter(command, "min_lon", query.Bbox.MinLon);
            AddParameter(command, "min_lat", query.Bbox.MinLat);
            AddParameter(command, "max_lon", query.Bbox.MaxLon);
            AddParameter(command, "max_lat", query.Bbox.MaxLat);
            AddParameter(
                command,
                "tolerance",
                MapFeaturesQuery.SimplifyTolerance(query.Zoom));

            if (query.Kinds.Count > 0)
            {
                AddParameter(command, "kinds", string.Join(',', query.Kinds));
            }

            if (query.CategoryIds.Count > 0)
            {
                AddParameter(command, "categories", string.Join(',', query.CategoryIds));
            }

            if (query.StatusCodes.Count > 0)
            {
                AddParameter(command, "statuses", string.Join(',', query.StatusCodes));
            }

            if (query.GroupIds.Count > 0)
            {
                AddParameter(command, "groups", string.Join(',', query.GroupIds));
            }

            return await ReadRowsAsync(command, ct);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static string BuildSql(MapFeaturesQuery query)
    {
        var filters = new List<string>();

        if (query.Kinds.Count > 0)
        {
            filters.Add("and mo.coverage_kind = any(string_to_array(@kinds, ','))");
        }

        if (query.CategoryIds.Count > 0)
        {
            filters.Add("and ot.category_id = any(string_to_array(@categories, ',')::uuid[])");
        }

        if (query.StatusCodes.Count > 0)
        {
            filters.Add("and ss.code = any(string_to_array(@statuses, ','))");
        }

        if (query.GroupIds.Count > 0 && query.IncludeObjectsWithoutGroup)
        {
            filters.Add(
                "and (mo.group_id = any(string_to_array(@groups, ',')::uuid[]) or mo.group_id is null)");
        }
        else if (query.GroupIds.Count > 0)
        {
            filters.Add("and mo.group_id = any(string_to_array(@groups, ',')::uuid[])");
        }
        else if (query.IncludeObjectsWithoutGroup)
        {
            filters.Add("and mo.group_id is null");
        }

        var filterSql = filters.Count == 0 ? string.Empty : string.Join("\n  ", filters);

        return $"""
            select
                mo.id,
                mo.name,
                mo.coverage_kind,
                ot.category_id,
                mo.object_type_id,
                mo.group_id,
                mo.address,
                coalesce(
                    ST_AsGeoJSON(
                        case when mo.coverage_kind = 'polygon'
                             then ST_Simplify(mo.geometry::geometry, @tolerance)
                             else mo.geometry::geometry
                        end,
                        6),
                    ST_AsGeoJSON(mo.geometry::geometry, 6)) as geometry_json,
                cs.source_status_id,
                ss.code as status_code,
                ss.name as status_name,
                ss.color as status_color,
                ss.icon as status_icon,
                cs.freshness,
                cs.observed_at,
                cs.source_type,
                coalesce(cs.observation_count, 0) as observation_count
            from map_objects mo
            left join object_types ot on ot.id = mo.object_type_id
            left join current_statuses cs on cs.map_object_id = mo.id
            left join source_statuses ss on ss.id = cs.source_status_id
            where mo.is_active
              and mo.geometry && ST_MakeEnvelope(@min_lon, @min_lat, @max_lon, @max_lat, 4326)::geography
              and ST_Intersects(
                    mo.geometry,
                    ST_MakeEnvelope(@min_lon, @min_lat, @max_lon, @max_lat, 4326)::geography)
              {filterSql}
            order by mo.id
            limit {MaxFeatures}
            """;
    }

    private static async Task<IReadOnlyList<MapFeatureRow>> ReadRowsAsync(
        DbCommand command,
        CancellationToken ct)
    {
        var rows = new List<MapFeatureRow>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new MapFeatureRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                ParseGeometry(reader.GetString(7)),
                reader.IsDBNull(8) ? null : reader.GetGuid(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.GetInt32(16)));
        }

        return rows;
    }

    private static JsonElement ParseGeometry(string geoJson)
    {
        using var document = JsonDocument.Parse(geoJson);

        return document.RootElement.Clone();
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
