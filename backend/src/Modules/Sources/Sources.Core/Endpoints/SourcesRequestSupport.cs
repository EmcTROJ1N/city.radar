using System.Text.Json;
using System.Text.RegularExpressions;

using BuildingBlocks.Web;

using Microsoft.AspNetCore.Http;

namespace Sources.Core.Endpoints;

/// <summary>
/// Разбор и проверка JSON-тел эндпоинтов источников.
/// Тело читается как <see cref="JsonDocument"/>, чтобы строго проверить набор полей
/// (<c>additionalProperties: false</c>) и вернуть 422 с <c>errors[field,message]</c>,
/// как в схеме <c>ValidationProblem</c>.
/// </summary>
internal static partial class SourcesRequestSupport
{
    /// <summary>Поля <c>SourceUpdate</c>; <c>statuses</c> меняются отдельными методами.</summary>
    private static readonly HashSet<string> UpdateFields =
        new(StringComparer.Ordinal)
        {
            "name", "sourceType", "chatId", "timeoutSeconds", "confidenceThreshold",
            "prefilter", "userPrompt", "linkByText",
        };

    /// <summary>Идентификатор чата: <c>@name</c>, числовой id или ссылка на чат.</summary>
    private static readonly string[] TelegramHosts = ["t.me", "www.t.me", "telegram.me", "www.telegram.me"];
    private static readonly string[] MaxHosts = ["max.ru", "www.max.ru"];

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
    /// Разбирает <c>SourceCreate</c>/<c>SourceUpdate</c>. Обязательны <c>name</c>,
    /// <c>sourceType</c>, <c>chatId</c> и <c>timeoutSeconds</c>; <c>userPrompt</c> по умолчанию
    /// пуст, <c>linkByText</c> — <c>true</c>. На создании <c>statuses</c> принимается,
    /// но пока игнорируется: статусы источника обрабатываются отдельно.
    /// </summary>
    public static SourceInput? ReadSource(
        JsonElement root,
        bool allowStatuses,
        List<ValidationError> errors)
    {
        RejectUnknownFields(root, allowStatuses, errors);

        if (allowStatuses && root.TryGetProperty("statuses", out var statuses)
            && statuses.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
        {
            errors.Add(new ValidationError("statuses", "Ожидается массив статусов источника"));
        }

        var name = ReadName(root, errors);
        var sourceType = ReadRequiredString(root, "sourceType", errors);
        var isSite = string.Equals(sourceType, "site", StringComparison.Ordinal);
        var chatId = isSite
            ? ReadOptionalChatId(root, errors)
            : ReadRequiredString(root, "chatId", errors);
        var timeoutSeconds = ReadTimeoutSeconds(root, errors);
        var confidenceThreshold = ReadConfidenceThreshold(root, errors);
        var prefilter = ReadPrefilter(root, errors);
        var userPrompt = ReadPrompt(root, errors);
        var linkByText = ReadOptionalBool(root, "linkByText", errors) ?? !isSite;

        if (!isSite && sourceType is not null && chatId is not null && !IsValidChatId(sourceType, chatId))
        {
            errors.Add(new ValidationError(
                "chatId",
                "Некорректный идентификатор чата: допустимы @name (5–32 латинских символа, цифры, _), "
                + "числовой id или ссылка https://t.me/… / https://max.ru/…"));
        }

        return errors.Count == 0
            ? new SourceInput(
                name!,
                sourceType!,
                chatId!,
                timeoutSeconds!.Value,
                confidenceThreshold,
                prefilter,
                userPrompt!,
                linkByText)
            : null;
    }

    /// <summary>
    /// Формат идентификатора чата зависит от типа адаптера: <c>@name</c>, числовой id
    /// (может быть отрицательным) или ссылка на чат соответствующего сервиса.
    /// </summary>
    private static bool IsValidChatId(string sourceType, string chatId)
    {
        var value = chatId.Trim();

        if (ChatHandlePattern().IsMatch(value) || NumericChatIdPattern().IsMatch(value))
        {
            return true;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.AbsolutePath.Trim('/')))
        {
            return false;
        }

        var host = uri.Host;
        var hosts = sourceType switch
        {
            "telegram_chat" => TelegramHosts,
            "max_chat" => MaxHosts,
            // Неизвестный тип уже даёт 422 по полю sourceType; ссылку принимаем любую из известных.
            _ => [.. TelegramHosts, .. MaxHosts],
        };

        return hosts.Contains(host, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Название обязательно.</summary>
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

    /// <summary>
    /// Таймаут актуальности: обязательное целое, строго больше нуля. Верхней границы контракт
    /// не задаёт; <c>TimeSpan.FromSeconds</c> вмещает весь диапазон <c>int</c>.
    /// </summary>
    private const int MinTimeoutSeconds = 60;

    private static int? ReadTimeoutSeconds(JsonElement root, List<ValidationError> errors)
    {
        if (!root.TryGetProperty("timeoutSeconds", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            errors.Add(new ValidationError("timeoutSeconds", "Поле timeoutSeconds обязательно"));

            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var seconds))
        {
            errors.Add(new ValidationError("timeoutSeconds", "Ожидается целое число секунд"));

            return null;
        }

        // Нижняя граница из контракта (minimum: 60): короче минуты таймаут актуальности бессмыслен.
        if (seconds < MinTimeoutSeconds)
        {
            errors.Add(new ValidationError("timeoutSeconds", "Таймаут должен быть не менее 60 секунд"));

            return null;
        }

        return seconds;
    }

    /// <summary>Порог уверенности LLM: необязательный, <c>null</c> — значение по умолчанию; 0..1.</summary>
    private static float? ReadConfidenceThreshold(JsonElement root, List<ValidationError> errors)
    {
        if (!root.TryGetProperty("confidenceThreshold", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var threshold))
        {
            errors.Add(new ValidationError("confidenceThreshold", "Ожидается число от 0 до 1"));

            return null;
        }

        if (threshold is < 0 or > 1)
        {
            errors.Add(new ValidationError("confidenceThreshold", "Порог уверенности должен быть от 0 до 1"));

            return null;
        }

        return (float)threshold;
    }

    /// <summary>Префильтр до LLM: необязательный JSON-объект (jsonb).</summary>
    private static string? ReadPrefilter(JsonElement root, List<ValidationError> errors)
    {
        if (!root.TryGetProperty("prefilter", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new ValidationError("prefilter", "Ожидается JSON-объект префильтра"));

            return null;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (property.Name is not ("keywords" or "regex"))
            {
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.Array
                || property.Value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            {
                errors.Add(new ValidationError(
                    $"prefilter.{property.Name}",
                    "Ожидается массив строк"));

                return null;
            }
        }

        return value.GetRawText();
    }

    /// <summary>Промпт-фильтр: необязательный текст.</summary>
    private static string? ReadPrompt(JsonElement root, List<ValidationError> errors)
    {
        if (!root.TryGetProperty("userPrompt", out var value))
        {
            return string.Empty;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            errors.Add(new ValidationError("userPrompt", "Поле не может быть null"));

            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add(new ValidationError("userPrompt", "Ожидается строка"));

            return null;
        }

        return value.GetString() ?? string.Empty;
    }

    private static void RejectUnknownFields(
        JsonElement root,
        bool allowStatuses,
        List<ValidationError> errors)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!UpdateFields.Contains(property.Name)
                && !(allowStatuses && string.Equals(property.Name, "statuses", StringComparison.Ordinal)))
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

    /// <summary>
    /// Идентификатор чата у системного «Сайта»: чата нет, поэтому поле необязательно.
    /// Отсутствие или <c>null</c> — пустая строка.
    /// </summary>
    private static string? ReadOptionalChatId(JsonElement root, List<ValidationError> errors)
    {
        if (!root.TryGetProperty("chatId", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add(new ValidationError("chatId", "Ожидается строка"));

            return null;
        }

        return value.GetString() ?? string.Empty;
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

    [GeneratedRegex("^@[A-Za-z0-9_]{5,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ChatHandlePattern();

    [GeneratedRegex("^-?[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericChatIdPattern();
}

/// <summary>Тело <c>SourceCreate</c>/<c>SourceUpdate</c> после проверки.</summary>
internal sealed record SourceInput(
    string Name,
    string SourceType,
    string ChatId,
    int TimeoutSeconds,
    float? ConfidenceThreshold,
    string? Prefilter,
    string UserPrompt,
    bool LinkByText);
