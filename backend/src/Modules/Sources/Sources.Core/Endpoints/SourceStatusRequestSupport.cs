using System.Text.Json;
using System.Text.RegularExpressions;

using BuildingBlocks.Web;

using Microsoft.AspNetCore.Http;

namespace Sources.Core.Endpoints;

/// <summary>
/// Разбор и проверка JSON-тел эндпоинтов статусов источника: строгий
/// набор полей (<c>additionalProperties: false</c>) и 422 с <c>errors[field,message]</c>,
/// как в схеме <c>ValidationProblem</c>.
/// </summary>
internal static partial class SourceStatusRequestSupport
{
    /// <summary>Поля <c>SourceStatusInput</c> из контракта.</summary>
    private static readonly HashSet<string> InputFields =
        new(StringComparer.Ordinal)
        {
            "code", "name", "description", "color", "icon", "sortOrder", "statusDefinitionId",
        };

    /// <summary>Палитра цветов статуса из контракта и check-констрейнта БД.</summary>
    private static readonly HashSet<string> StatusColors =
        new(StringComparer.Ordinal) { "ok", "limited", "none", "queue", "schedule", "stale" };

    /// <summary>
    /// Разбирает <c>SourceStatusInput</c>. При создании из записи справочника
    /// (<c>statusDefinitionId</c>) <c>code</c>, <c>name</c>, <c>color</c> и <c>icon</c>
    /// копируются из неё и в теле необязательны; для собственного статуса — обязательны.
    /// </summary>
    public static SourceStatusInput? Read(
        JsonElement root,
        bool coreFieldsRequired,
        List<ValidationError> errors)
    {
        RejectUnknownFields(root, errors);

        var statusDefinitionSpecified = root.TryGetProperty("statusDefinitionId", out _);
        var statusDefinitionId = ReadOptionalGuid(root, "statusDefinitionId", errors);

        var code = ReadCode(root, coreFieldsRequired, errors);
        var name = ReadName(root, coreFieldsRequired, errors);
        var description = ReadDescription(root, errors);
        var color = ReadColor(root, coreFieldsRequired, errors);
        var icon = ReadIcon(root, coreFieldsRequired, errors);
        var sortOrder = ReadOptionalInt(root, "sortOrder", errors) ?? 0;

        return errors.Count == 0
            ? new SourceStatusInput(
                code,
                name,
                description,
                color,
                icon,
                sortOrder,
                statusDefinitionId,
                statusDefinitionSpecified)
            : null;
    }

    private static void RejectUnknownFields(JsonElement root, List<ValidationError> errors)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!InputFields.Contains(property.Name))
            {
                errors.Add(new ValidationError(property.Name, "Неизвестное поле"));
            }
        }
    }

    /// <summary>Код по контракту: <c>^[a-z0-9_]+$</c>, уникален внутри источника.</summary>
    private static string? ReadCode(
        JsonElement root,
        bool required,
        List<ValidationError> errors)
    {
        var code = ReadOptionalString(root, "code", errors, required);
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

    /// <summary>Название — непустой текст.</summary>
    private static string? ReadName(
        JsonElement root,
        bool required,
        List<ValidationError> errors)
    {
        var name = ReadOptionalString(root, "name", errors, required);
        if (name is null)
        {
            return null;
        }

        if (name.Length == 0)
        {
            errors.Add(new ValidationError("name", "Поле name не может быть пустым"));

            return null;
        }

        if (!TextValidator.IsValid(name))
        {
            errors.Add(new ValidationError("name", TextValidator.InvalidCharactersMessage));

            return null;
        }

        return name;
    }

    /// <summary>Описание для LLM: необязательный текст.</summary>
    private static string? ReadDescription(JsonElement root, List<ValidationError> errors)
    {
        var description = ReadOptionalString(root, "description", errors, required: false);

        if (description is not null && !TextValidator.IsValid(description))
        {
            errors.Add(new ValidationError("description", TextValidator.InvalidCharactersMessage));

            return null;
        }

        return description;
    }

    private static string? ReadColor(
        JsonElement root,
        bool required,
        List<ValidationError> errors)
    {
        var color = ReadOptionalString(root, "color", errors, required);
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

    private static string? ReadIcon(
        JsonElement root,
        bool required,
        List<ValidationError> errors)
    {
        var icon = ReadOptionalString(root, "icon", errors, required);
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

    /// <summary>
    /// Необязательная строка. При <paramref name="required"/> отсутствие поля или <c>null</c> —
    /// ошибка «поле обязательно» (так <c>code</c>/<c>name</c>/<c>color</c>/<c>icon</c> нужны
    /// для собственного статуса и не нужны при копировании из справочника).
    /// </summary>
    private static string? ReadOptionalString(
        JsonElement root,
        string field,
        List<ValidationError> errors,
        bool required)
    {
        if (!root.TryGetProperty(field, out var value))
        {
            if (required)
            {
                errors.Add(new ValidationError(field, $"Поле {field} обязательно"));
            }

            return null;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            errors.Add(new ValidationError(field, "Поле не может быть null"));

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
        if (required && string.IsNullOrWhiteSpace(text))
        {
            errors.Add(new ValidationError(field, $"Поле {field} обязательно"));

            return null;
        }

        return text;
    }

    private static Guid? ReadOptionalGuid(
        JsonElement root,
        string field,
        List<ValidationError> errors)
    {
        if (!root.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out var id))
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

    [GeneratedRegex("^[a-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[a-z0-9-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IconPattern();
}

/// <summary>
/// Тело <c>SourceStatusInput</c> после проверки. <c>Code</c>/<c>Name</c>/<c>Color</c>/<c>Icon</c>
/// равны <c>null</c> только при создании из справочника: тогда значения копируются из него.
/// </summary>
internal sealed record SourceStatusInput(
    string? Code,
    string? Name,
    string? Description,
    string? Color,
    string? Icon,
    int SortOrder,
    Guid? StatusDefinitionId,
    bool StatusDefinitionSpecified);
