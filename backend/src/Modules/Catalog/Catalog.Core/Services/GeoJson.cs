using System.Text.Json;

using BuildingBlocks.Web;

using NetTopologySuite;
using NetTopologySuite.Geometries;

namespace Catalog.Core.Services;

/// <summary>
/// GeoJSON для геометрии фиксаций: разбор входа и сериализация выхода.
/// Контракт допускает только <c>Point</c> и <c>Polygon</c>, поэтому остальные типы GeoJSON
/// отклоняются 422. Координаты — <c>[долгота, широта]</c> в WGS 84 (SRID 4326).
/// </summary>
/// <remarks>
/// Разбор ручной: нужно не только построить геометрию, но и вернуть точную ошибку поля
/// <c>geometry</c> (замкнутость кольца, диапазон координат, самопересечение), которую
/// конвертер GeoJSON не даёт. NTS используется только для модели и проверки валидности.
/// </remarks>
internal static class GeoJson
{
    /// <summary>SRID эталона: <c>geography(Geometry, 4326)</c>.</summary>
    public const int Srid = 4326;

    private const string GeometryField = "geometry";

    private const string PointKind = "point";
    private const string PolygonKind = "polygon";

    private static readonly GeometryFactory Factory =
        NtsGeometryServices.Instance.CreateGeometryFactory(srid: Srid);

    /// <summary>
    /// Разбирает GeoJSON-объект и проверяет его соответствие типу покрытия.
    /// Ошибки добавляются в <paramref name="errors"/> под полем <c>geometry</c>.
    /// </summary>
    /// <param name="value">JSON-значение поля <c>geometry</c>.</param>
    /// <param name="coverageKind">Тип покрытия запроса; <c>null</c>, если он сам невалиден.</param>
    /// <param name="errors">Накопитель ошибок валидации.</param>
    /// <param name="geometry">Построенная геометрия или <c>null</c> при ошибке.</param>
    public static bool TryParse(
        JsonElement value,
        string? coverageKind,
        List<ValidationError> errors,
        out Geometry? geometry)
    {
        geometry = null;

        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new ValidationError(GeometryField, "Ожидается GeoJSON-объект Point или Polygon"));

            return false;
        }

        if (!value.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String)
        {
            errors.Add(new ValidationError(GeometryField, "GeoJSON-объект должен содержать поле type"));

            return false;
        }

        var type = typeElement.GetString();
        var parsed = type switch
        {
            "Point" => TryParsePoint(value, errors, out geometry),
            "Polygon" => TryParsePolygon(value, errors, out geometry),
            _ => RejectUnsupportedType(type, errors),
        };

        if (!parsed || geometry is null)
        {
            geometry = null;

            return false;
        }

        // Тип объекта с geometryKind=point не принимает полигон и наоборот.
        // any — любое покрытие.
        if (coverageKind == PointKind && geometry is not Point)
        {
            geometry = null;
            errors.Add(new ValidationError(
                GeometryField,
                "Тип покрытия point принимает только геометрию Point"));

            return false;
        }

        if (coverageKind == PolygonKind && geometry is not Polygon)
        {
            geometry = null;
            errors.Add(new ValidationError(
                GeometryField,
                "Тип покрытия polygon принимает только геометрию Polygon"));

            return false;
        }

        geometry.SRID = Srid;

        return true;
    }

    /// <summary>Сериализует геометрию фиксации в GeoJSON для ответа API.</summary>
    public static JsonElement ToGeoJson(Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        return geometry switch
        {
            Point point => JsonSerializer.SerializeToElement(new
            {
                type = "Point",
                coordinates = new[] { point.X, point.Y },
            }),
            Polygon polygon => JsonSerializer.SerializeToElement(new
            {
                type = "Polygon",
                coordinates = SerializeRings(polygon),
            }),
            _ => throw new InvalidOperationException(
                $"Геометрия типа {geometry.GeometryType} не поддерживается контрактом"),
        };
    }

    private static bool TryParsePoint(
        JsonElement value,
        List<ValidationError> errors,
        out Geometry? geometry)
    {
        geometry = null;

        if (!value.TryGetProperty("coordinates", out var coordinates)
            || coordinates.ValueKind != JsonValueKind.Array
            || coordinates.GetArrayLength() != 2)
        {
            errors.Add(new ValidationError(
                GeometryField,
                "Coordinates точки — массив [долгота, широта]"));

            return false;
        }

        if (!TryParsePosition(coordinates[0], coordinates[1], errors, out var coordinate))
        {
            return false;
        }

        geometry = Factory.CreatePoint(coordinate);

        return true;
    }

    private static bool TryParsePolygon(
        JsonElement value,
        List<ValidationError> errors,
        out Geometry? geometry)
    {
        geometry = null;

        if (!value.TryGetProperty("coordinates", out var rings)
            || rings.ValueKind != JsonValueKind.Array
            || rings.GetArrayLength() == 0)
        {
            errors.Add(new ValidationError(
                GeometryField,
                "Coordinates полигона — массив колец"));

            return false;
        }

        var shells = new List<LinearRing>(rings.GetArrayLength());

        foreach (var ring in rings.EnumerateArray())
        {
            if (!TryParseRing(ring, errors, out var linearRing))
            {
                return false;
            }

            shells.Add(linearRing!);
        }

        try
        {
            geometry = Factory.CreatePolygon(shells[0], [.. shells.Skip(1)]);
        }
        catch (ArgumentException)
        {
            errors.Add(new ValidationError(GeometryField, "Некорректный полигон"));

            return false;
        }

        // Замкнутость проверена при разборе кольца; здесь — самопересечения и вырожденность.
        if (!geometry.IsValid)
        {
            geometry = null;
            errors.Add(new ValidationError(
                GeometryField,
                "Полигон невалиден: кольца не должны самопересекаться"));

            return false;
        }

        return true;
    }

    private static bool TryParseRing(
        JsonElement ring,
        List<ValidationError> errors,
        out LinearRing? linearRing)
    {
        linearRing = null;

        if (ring.ValueKind != JsonValueKind.Array || ring.GetArrayLength() < 4)
        {
            errors.Add(new ValidationError(
                GeometryField,
                "Кольцо полигона содержит не менее 4 координат"));

            return false;
        }

        var coordinates = new List<Coordinate>(ring.GetArrayLength());

        foreach (var position in ring.EnumerateArray())
        {
            if (position.ValueKind != JsonValueKind.Array || position.GetArrayLength() != 2)
            {
                errors.Add(new ValidationError(
                    GeometryField,
                    "Координата — массив [долгота, широта]"));

                return false;
            }

            if (!TryParsePosition(position[0], position[1], errors, out var coordinate))
            {
                return false;
            }

            coordinates.Add(coordinate);
        }

        if (!coordinates[0].Equals2D(coordinates[^1]))
        {
            errors.Add(new ValidationError(
                GeometryField,
                "Кольцо полигона должно быть замкнутым: первая и последняя координаты совпадают"));

            return false;
        }

        try
        {
            linearRing = Factory.CreateLinearRing([.. coordinates]);
        }
        catch (ArgumentException)
        {
            errors.Add(new ValidationError(GeometryField, "Некорректное кольцо полигона"));

            return false;
        }

        return true;
    }

    private static bool TryParsePosition(
        JsonElement longitudeElement,
        JsonElement latitudeElement,
        List<ValidationError> errors,
        out Coordinate coordinate)
    {
        coordinate = null!;

        if (!TryReadNumber(longitudeElement, out var longitude)
            || !TryReadNumber(latitudeElement, out var latitude))
        {
            errors.Add(new ValidationError(GeometryField, "Координаты должны быть числами"));

            return false;
        }

        if (longitude is < -180 or > 180)
        {
            errors.Add(new ValidationError(
                GeometryField,
                "Долгота должна быть в диапазоне от -180 до 180"));

            return false;
        }

        if (latitude is < -90 or > 90)
        {
            errors.Add(new ValidationError(
                GeometryField,
                "Широта должна быть в диапазоне от -90 до 90"));

            return false;
        }

        coordinate = new Coordinate(longitude, latitude);

        return true;
    }

    private static bool TryReadNumber(JsonElement element, out double number)
    {
        number = 0;

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out number))
        {
            return false;
        }

        return double.IsFinite(number);
    }

    private static bool RejectUnsupportedType(string? type, List<ValidationError> errors)
    {
        errors.Add(new ValidationError(
            GeometryField,
            $"Поддерживаются только GeoJSON Point и Polygon, получено '{type ?? "null"}'"));

        return false;
    }

    private static double[][][] SerializeRings(Polygon polygon)
    {
        var rings = new List<double[][]>(polygon.NumInteriorRings + 1)
        {
            SerializeRing(polygon.ExteriorRing),
        };

        for (var index = 0; index < polygon.NumInteriorRings; index++)
        {
            rings.Add(SerializeRing(polygon.GetInteriorRingN(index)));
        }

        return [.. rings];
    }

    private static double[][] SerializeRing(LineString ring) =>
        [.. ring.Coordinates.Select(coordinate => new[] { coordinate.X, coordinate.Y })];
}
