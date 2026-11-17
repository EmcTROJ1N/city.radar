using System.Text.Json;
using System.Text.RegularExpressions;

using BuildingBlocks.Web;

using Catalog.Core.Services;

using Microsoft.AspNetCore.Http;

namespace Catalog.Core.Endpoints;

/// <summary>
/// Разбор и проверка JSON-тел эндпоинтов каталога. Тело читается как
/// <see cref="JsonDocument"/>, чтобы строго проверить набор полей
/// (<c>additionalProperties: false</c>) и вернуть 422 с <c>errors[field,message]</c>,
/// как в схеме <c>ValidationProblem</c>.
/// </summary>
internal static partial class CatalogRequestSupport
{
    private static readonly HashSet<string> CategoryFields =
        new(StringComparer.Ordinal) { "code", "name", "icon", "sortOrder", "isActive" };

    private static readonly HashSet<string> ObjectTypeFields =
        new(StringComparer.Ordinal)
        {
            "categoryId", "code", "name", "geometryKind",
        };

    private static readonly HashSet<string> ObjectGroupFields =
        new(StringComparer.Ordinal) { "categoryId", "name", "color", "isVisibleByDefault" };

    private static readonly HashSet<string> StatusDefinitionFields =
        new(StringComparer.Ordinal)
        {
            "categoryId", "code", "name", "color", "icon", "severity", "isPositive",
        };

    /// <summary>Палитра цветов статуса из контракта и check-констрейнта БД.</summary>
    private static readonly HashSet<string> StatusColors =
        new(StringComparer.Ordinal) { "ok", "limited", "none", "queue", "schedule", "stale" };

    /// <summary>Допустимые виды геометрии типа объекта.</summary>
    private static readonly HashSet<string> GeometryKinds =
        new(StringComparer.Ordinal) { "point", "polygon", "any" };

    /// <summary>Типы покрытия фиксации из контракта.</summary>
    private static readonly HashSet<string> CoverageKinds =
        new(StringComparer.Ordinal) { "point", "polygon" };

    private static readonly HashSet<string> MapObjectFields =
        new(StringComparer.Ordinal)
        {
            "name", "objectTypeId", "groupId", "coverageKind", "geometry",
            "address", "isActive", "sourceIds",
        };

    /// <summary>Читает тело запроса как JSON-объект; <c>null</c> — тело не объект или не JSON.</summary>
    public static async Task<JsonDocument?> TryReadObjectAsync(HttpRequest request, CancellationToken ct)
    {
        try
        {
            var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Разбирает <c>CategoryInput</c>: <c>code</c> и <c>name</c> обязательны, остальные поля
    /// необязательны и имеют значения по умолчанию из контракта.
    /// </summary>
    public static CategoryInput? ReadCategory(
        JsonElement root,
        List<ValidationError> errors,
        out string? code)
    {
        RejectUnknownFields(root, CategoryFields, errors);

        code = ReadCode(root, errors);
        var name = ReadName(root, errors);
        var icon = ReadOptionalString(root, "icon", errors);
        var sortOrder = ReadOptionalInt(root, "sortOrder", errors) ?? 0;
        var isActive = ReadOptionalBool(root, "isActive", errors) ?? true;

        return errors.Count == 0
            ? new CategoryInput(code!, name!, icon, sortOrder, isActive)
            : null;
    }

    /// <summary>
    /// Разбирает <c>ObjectTypeInput</c>: <c>categoryId</c>, <c>code</c>, <c>name</c> обязательны.
    /// </summary>
    public static ObjectTypeInput? ReadObjectType(
        JsonElement root,
        List<ValidationError> errors,
        out string? code)
    {
        RejectUnknownFields(root, ObjectTypeFields, errors);

        var categoryId = ReadRequiredGuid(root, "categoryId", errors);
        code = ReadCode(root, errors);
        var name = ReadName(root, errors);
        var geometryKind = ReadGeometryKind(root, errors);

        return errors.Count == 0
            ? new ObjectTypeInput(categoryId!.Value, code!, name!, geometryKind!)
            : null;
    }

    /// <summary>
    /// Разбирает <c>ObjectGroupInput</c>: обязательно только <c>name</c>; <c>categoryId</c> и
    /// <c>color</c> допускают <c>null</c>.
    /// </summary>
    public static ObjectGroupInput? ReadObjectGroup(JsonElement root, List<ValidationError> errors)
    {
        RejectUnknownFields(root, ObjectGroupFields, errors);

        var categoryId = ReadOptionalGuid(root, "categoryId", errors);
        var name = ReadName(root, errors);
        var color = ReadOptionalString(root, "color", errors);
        var isVisibleByDefault = ReadOptionalBool(root, "isVisibleByDefault", errors) ?? true;

        return errors.Count == 0
            ? new ObjectGroupInput(categoryId, name!, color, isVisibleByDefault)
            : null;
    }

    /// <summary>
    /// Разбирает <c>StatusDefinitionInput</c>: <c>code</c>, <c>name</c>, <c>color</c> и
    /// <c>icon</c> обязательны, цвет — только из палитры.
    /// </summary>
    public static StatusDefinitionInput? ReadStatusDefinition(
        JsonElement root,
        List<ValidationError> errors)
    {
        RejectUnknownFields(root, StatusDefinitionFields, errors);

        var categoryId = ReadOptionalGuid(root, "categoryId", errors);
        var code = ReadCode(root, errors);
        var name = ReadName(root, errors);
        var color = ReadColor(root, errors);
        var icon = ReadIcon(root, errors);
        var severity = ReadOptionalInt(root, "severity", errors) ?? 0;
        var isPositive = ReadOptionalBool(root, "isPositive", errors) ?? false;

        return errors.Count == 0
            ? new StatusDefinitionInput(categoryId, code!, name!, color!, icon!, severity, isPositive)
            : null;
    }

    /// <summary>
    /// Разбирает <c>MapObjectInput</c>: <c>name</c>, <c>coverageKind</c> и <c>geometry</c>
    /// обязательны; геометрия проверяется на соответствие типу покрытия и диапазонам координат.
    /// </summary>
    public static MapObjectInput? ReadMapObject(JsonElement root, List<ValidationError> errors)
    {
        RejectUnknownFields(root, MapObjectFields, errors);

        var name = ReadName(root, errors);
        var objectTypeId = ReadOptionalGuid(root, "objectTypeId", errors);
        var groupId = ReadOptionalGuid(root, "groupId", errors);
        var coverageKind = ReadCoverageKind(root, errors);
        var geometry = ReadGeometry(root, coverageKind, errors);
        var address = ReadAddress(root, errors);
        var isActive = ReadOptionalBool(root, "isActive", errors) ?? true;
        var sourceIds = ReadSourceIds(root, errors);

        return errors.Count == 0
            ? new MapObjectInput(
                name!,
                objectTypeId,
                groupId,
                coverageKind!,
                geometry!,
                address,
                isActive,
                sourceIds!)
            : null;
    }

    /// <summary>Тип покрытия: обязателен, <c>point</c> или <c>polygon</c>.</summary>
    private static string? ReadCoverageKind(JsonElement root, List<ValidationError> errors)
    {
        var kind = ReadRequiredString(root, "coverageKind", errors);
        if (kind is null)
        {
            return null;
        }

        if (!CoverageKinds.Contains(kind))
        {
            errors.Add(new ValidationError(
                "coverageKind",
                "Допустимые значения: point, polygon"));

            return null;
        }

        return kind;
    }

    /// <summary>Геометрия — обязательный GeoJSON Point или Polygon (см. <see cref="GeoJson"/>).</summary>
    private static NetTopologySuite.Geometries.Geometry? ReadGeometry(
        JsonElement root,
        string? coverageKind,
        List<ValidationError> errors)
    {
        if (!root.TryGetProperty("geometry", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            errors.Add(new ValidationError("geometry", "Поле geometry обязательно"));

            return null;
        }

        return GeoJson.TryParse(value, coverageKind, errors, out var geometry) ? geometry : null;
    }

    /// <summary>Адрес необязателен.</summary>
    private static string? ReadAddress(JsonElement root, List<ValidationError> errors)
    {
        var address = ReadOptionalString(root, "address", errors);
        if (address is not null && !TextValidator.IsValid(address))
        {
            errors.Add(new ValidationError("address", TextValidator.InvalidCharactersMessage));

            return null;
        }

        return address;
    }

    /// <summary>
    /// Источники, привязываемые при создании: массив uuid без дублей (<c>uniqueItems</c>).
    /// </summary>
    private static IReadOnlyList<Guid>? ReadSourceIds(JsonElement root, List<ValidationError> errors)
    {
        if (!root.TryGetProperty("sourceIds", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new ValidationError("sourceIds", "Ожидается массив uuid"));

            return null;
        }

        var sourceIds = new List<Guid>(value.GetArrayLength());

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !Guid.TryParse(item.GetString(), out var sourceId))
            {
                errors.Add(new ValidationError("sourceIds", "Ожидается массив uuid"));

                return null;
            }

            if (sourceIds.Contains(sourceId))
            {
                errors.Add(new ValidationError("sourceIds", "Источники не должны повторяться"));

                return null;
            }

            sourceIds.Add(sourceId);
        }

        return sourceIds;
    }

    /// <summary>Код по контракту: <c>^[a-z0-9_]+$</c>, обязателен.</summary>
    private static string? ReadCode(JsonElement root, List<ValidationError> errors)
    {
        var code = ReadRequiredString(root, "code", errors);
        if (code is null)
        {
            return null;
        }

        if (!CodePattern().IsMatch(code))
        {
            errors.Add(new ValidationError(
                "code",
                "Код должен состоять из строчных латинских букв, цифр и знака подчёркивания"));

            return null;
        }

        return code;
    }

    /// <summary>
    /// Название обязательно и проверяется на допустимые символы.
    /// </summary>
    private static string? ReadName(JsonElement root, List<ValidationError> errors)
    {
        var name = ReadRequiredString(root, "name", errors);
        if (name is null)
        {
            return null;
        }

        if (!TextValidator.IsValid(name))
        {
            errors.Add(new ValidationError("name", TextValidator.InvalidCharactersMessage));

            return null;
        }

        return name;
    }

    private static string? ReadColor(JsonElement root, List<ValidationError> errors)
    {
        var color = ReadRequiredString(root, "color", errors);
        if (color is null)
        {
            return null;
        }

        if (!StatusColors.Contains(color))
        {
            errors.Add(new ValidationError(
                "color",
                "Цвет должен быть одним из: ok, limited, none, queue, schedule, stale"));

            return null;
        }

        return color;
    }

    private static string? ReadIcon(JsonElement root, List<ValidationError> errors)
    {
        var icon = ReadRequiredString(root, "icon", errors);
        if (icon is null)
        {
            return null;
        }

        if (!IconPattern().IsMatch(icon))
        {
            errors.Add(new ValidationError(
                "icon",
                "Иконка должна состоять из строчных латинских букв, цифр и дефиса"));

            return null;
        }

        return icon;
    }

    private static string? ReadGeometryKind(JsonElement root, List<ValidationError> errors)
    {
        if (!root.TryGetProperty("geometryKind", out var value))
        {
            return "any";
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            errors.Add(new ValidationError("geometryKind", "Поле не может быть null"));

            return null;
        }

        var kind = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (kind is null || !GeometryKinds.Contains(kind))
        {
            errors.Add(new ValidationError(
                "geometryKind",
                "Допустимые значения: point, polygon, any"));

            return null;
        }

        return kind;
    }

    private static void RejectUnknownFields(
        JsonElement root,
        HashSet<string> allowed,
        List<ValidationError> errors)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                errors.Add(new ValidationError(property.Name, "Неизвестное поле"));
            }
        }
    }

    private static string? ReadRequiredString(
        JsonElement root,
        string field,
        List<ValidationError> errors)
    {
        if (!root.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            errors.Add(new ValidationError(field, $"Поле {field} обязательно"));

            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add(new ValidationError(field, "Ожидается строка"));

            return null;
        }

        var text = value.GetString();
        if (!TextValidator.IsStorable(text))
        {
            errors.Add(new ValidationError(field, TextValidator.UnstorableMessage));

            return null;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            errors.Add(new ValidationError(field, $"Поле {field} обязательно"));

            return null;
        }

        return text;
    }

    /// <summary>Необязательная строка; отсутствие поля и <c>null</c> означают <c>null</c>.</summary>
    private static string? ReadOptionalString(
        JsonElement root,
        string field,
        List<ValidationError> errors)
    {
        if (!root.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add(new ValidationError(field, "Ожидается строка"));

            return null;
        }

        return value.GetString();
    }

    private static Guid? ReadRequiredGuid(
        JsonElement root,
        string field,
        List<ValidationError> errors)
    {
        if (!root.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            errors.Add(new ValidationError(field, $"Поле {field} обязательно"));

            return null;
        }

        return ParseGuid(value, field, errors);
    }

    /// <summary>Необязательный <c>uuid</c>; отсутствие поля и <c>null</c> означают <c>null</c>.</summary>
    private static Guid? ReadOptionalGuid(
        JsonElement root,
        string field,
        List<ValidationError> errors)
    {
        if (!root.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return ParseGuid(value, field, errors);
    }

    private static Guid? ParseGuid(JsonElement value, string field, List<ValidationError> errors)
    {
        if (value.ValueKind != JsonValueKind.String
            || !Guid.TryParse(value.GetString(), out var id))
        {
            errors.Add(new ValidationError(field, "Ожидается uuid"));

            return null;
        }

        return id;
    }

    private static int? ReadOptionalInt(
        JsonElement root,
        string field,
        List<ValidationError> errors)
    {
        if (!root.TryGetProperty(field, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            errors.Add(new ValidationError(field, "Поле не может быть null"));

            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            errors.Add(new ValidationError(field, "Ожидается целое число"));

            return null;
        }

        return number;
    }

    private static bool? ReadOptionalBool(
        JsonElement root,
        string field,
        List<ValidationError> errors)
    {
        if (!root.TryGetProperty(field, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            errors.Add(new ValidationError(field, "Поле не может быть null"));

            return null;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            errors.Add(new ValidationError(field, "Ожидается логическое значение"));

            return null;
        }

        return value.GetBoolean();
    }

    [GeneratedRegex("^[a-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[a-z0-9-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IconPattern();
}

/// <summary>Тело <c>CategoryInput</c> после проверки.</summary>
internal sealed record CategoryInput(string Code, string Name, string? Icon, int SortOrder, bool IsActive);

/// <summary>Тело <c>ObjectTypeInput</c> после проверки.</summary>
internal sealed record ObjectTypeInput(
    Guid CategoryId,
    string Code,
    string Name,
    string GeometryKind);

/// <summary>Тело <c>ObjectGroupInput</c> после проверки.</summary>
internal sealed record ObjectGroupInput(
    Guid? CategoryId,
    string Name,
    string? Color,
    bool IsVisibleByDefault);

/// <summary>Тело <c>StatusDefinitionInput</c> после проверки.</summary>
internal sealed record StatusDefinitionInput(
    Guid? CategoryId,
    string Code,
    string Name,
    string Color,
    string Icon,
    int Severity,
    bool IsPositive);

/// <summary>Тело <c>MapObjectInput</c> после структурной проверки (ссылки — в сервисе).</summary>
internal sealed record MapObjectInput(
    string Name,
    Guid? ObjectTypeId,
    Guid? GroupId,
    string CoverageKind,
    NetTopologySuite.Geometries.Geometry Geometry,
    string? Address,
    bool IsActive,
    IReadOnlyList<Guid> SourceIds);
