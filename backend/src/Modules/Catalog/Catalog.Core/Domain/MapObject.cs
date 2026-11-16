using NetTopologySuite.Geometries;

namespace Catalog.Core.Domain;

/// <summary>
/// Фиксация на карте (таблица <c>map_objects</c>): точка или геозона
/// с необязательными типом объекта, группой, адресом и JSONB-атрибутами. Геометрия хранится
/// как PostGIS <c>geography(Geometry, 4326)</c>; тип геометрии обязан соответствовать
/// <see cref="CoverageKind"/> (check в БД).
/// </summary>
internal sealed class MapObject
{
    public Guid Id { get; set; }

    public Guid? ObjectTypeId { get; set; }

    public Guid? GroupId { get; set; }

    /// <summary>Название фиксации.</summary>
    public string Name { get; set; } = null!;

    /// <summary>Тип покрытия: <c>point</c> или <c>polygon</c>.</summary>
    public string CoverageKind { get; set; } = null!;

    /// <summary>Геометрия в WGS 84 (SRID 4326): <see cref="Point"/> или <see cref="Polygon"/>.</summary>
    public Geometry Geometry { get; set; } = null!;

    /// <summary>Атрибуты фиксации — JSON-объект, проверенный по схеме типа (jsonb).</summary>
    public string Attributes { get; set; } = "{}";

    /// <summary>Адрес; <c>null</c>, если не задан.</summary>
    public string? Address { get; set; }

    public bool IsActive { get; set; }

    /// <summary>Кто создал фиксацию; <c>null</c> для записей, созданных вне API.</summary>
    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Обновляется триггером <c>map_objects_updated</c> при любом UPDATE.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
