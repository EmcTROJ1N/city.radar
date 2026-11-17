using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using BuildingBlocks.Web;

namespace Sources.Core.Secrets;

/// <summary>
/// Разбор и проверка JSON-тел админских методов секретов и подключения источников
///: строгий набор полей
/// (<c>additionalProperties: false</c>) и 422 с <c>errors[field,message]</c>.
/// </summary>
internal static class SecretsRequestSupport
{
    private static readonly HashSet<string> TelegramSessionFields =
        new(StringComparer.Ordinal) { "name", "session", "mode", "proxyId" };

    private static readonly HashSet<string> ProxyFields =
        new(StringComparer.Ordinal) { "name", "scheme", "host", "port", "username", "password" };

    private static readonly HashSet<string> LlmFields =
        new(StringComparer.Ordinal) { "apiKey", "baseUrl", "model", "proxyId" };

    private static readonly HashSet<string> ConnectionFields =
        new(StringComparer.Ordinal) { "telegramSessionId", "proxyId" };

    /// <summary>Допустимые схемы прокси: SOCKS5, MTProxy и HTTP.</summary>
    private static readonly HashSet<string> ProxySchemes =
        new(StringComparer.Ordinal) { "socks5", "mtproxy", "http" };

    /// <summary>Разбирает тело <c>TelegramSessionSecret</c>.</summary>
    public static TelegramSessionInput? ReadTelegramSession(
        JsonElement root,
        List<ValidationError> errors)
    {
        RejectUnknownFields(root, TelegramSessionFields, errors);

        var name = ReadName(root, errors);
        var session = ReadRequiredString(root, "session", errors);
        var mode = ReadTelegramMode(root, errors);
        var proxyId = ReadOptionalGuid(root, "proxyId", errors);

        return errors.Count == 0
            ? new TelegramSessionInput(name!, session!, mode, proxyId)
            : null;
    }

    /// <summary>Разбирает тело <c>ProxySecret</c>: схема, хост и порт 1..65535.</summary>
    public static ProxyInput? ReadProxy(JsonElement root, List<ValidationError> errors)
    {
        RejectUnknownFields(root, ProxyFields, errors);

        var name = ReadName(root, errors);
        var scheme = ReadScheme(root, errors);
        var host = ReadRequiredString(root, "host", errors);
        var port = ReadPort(root, errors);
        var username = ReadOptionalString(root, "username", errors);
        var password = ReadOptionalString(root, "password", errors);

        return errors.Count == 0
            ? new ProxyInput(name!, scheme!, host!, port!.Value, username, password)
            : null;
    }

    /// <summary>Разбирает тело <c>LlmSecret</c>: ключ, абсолютный http(s) base URL и модель.</summary>
    public static LlmInput? ReadLlm(JsonElement root, List<ValidationError> errors)
    {
        RejectUnknownFields(root, LlmFields, errors);

        var apiKey = ReadRequiredString(root, "apiKey", errors);
        var baseUrl = ReadBaseUrl(root, errors);
        var model = ReadRequiredString(root, "model", errors);
        var proxyId = ReadOptionalGuid(root, "proxyId", errors);

        return errors.Count == 0
            ? new LlmInput(apiKey!, baseUrl!, model!, proxyId)
            : null;
    }

    /// <summary>Разбирает тело <c>SourceConnection</c>: отсутствующее поле снимает привязку.</summary>
    public static ConnectionInput? ReadConnection(JsonElement root, List<ValidationError> errors)
    {
        RejectUnknownFields(root, ConnectionFields, errors);

        var telegramSessionId = ReadOptionalGuid(root, "telegramSessionId", errors);
        var proxyId = ReadOptionalGuid(root, "proxyId", errors);

        return errors.Count == 0
            ? new ConnectionInput(telegramSessionId, proxyId)
            : null;
    }

    private static string? ReadName(JsonElement root, List<ValidationError> errors)
    {
        var name = ReadRequiredString(root, "name", errors);
        if (name is not null && !TextValidator.IsValid(name))
        {
            errors.Add(new ValidationError("name", TextValidator.InvalidCharactersMessage));

            return null;
        }

        return name;
    }

    private static string? ReadScheme(JsonElement root, List<ValidationError> errors)
    {
        var scheme = ReadRequiredString(root, "scheme", errors);
        if (scheme is not null && !ProxySchemes.Contains(scheme))
        {
            errors.Add(new ValidationError(
                "scheme",
                "Схема прокси должна быть socks5, mtproxy или http"));

            return null;
        }

        return scheme;
    }

    private static int? ReadPort(JsonElement root, List<ValidationError> errors)
    {
        if (!root.TryGetProperty("port", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            errors.Add(new ValidationError("port", "Поле port обязательно"));

            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var port))
        {
            errors.Add(new ValidationError("port", "Ожидается целое число от 1 до 65535"));

            return null;
        }

        if (port is < 1 or > 65535)
        {
            errors.Add(new ValidationError("port", "Порт должен быть от 1 до 65535"));

            return null;
        }

        return port;
    }

    private static string? ReadBaseUrl(JsonElement root, List<ValidationError> errors)
    {
        var baseUrl = ReadRequiredString(root, "baseUrl", errors);
        if (baseUrl is null)
        {
            return null;
        }

        if (!TryCreateHttpUri(baseUrl, out _))
        {
            errors.Add(new ValidationError(
                "baseUrl",
                "Ожидается абсолютный URL http:// или https://"));

            return null;
        }

        return baseUrl;
    }

    private static bool TryCreateHttpUri(string value, [NotNullWhen(true)] out Uri? uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

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

    private static string? ReadOptionalString(
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

        return string.IsNullOrEmpty(text) ? null : text;
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
            errors.Add(new ValidationError(field, "Ожидается UUID или null"));

            return null;
        }

        return id;
    }

    /// <summary>
    /// Способ входа Telegram-сессии. Поле необязательно: старые клиенты присылают только
    /// значение, а способ определяется по формату (токен бота или JSON MTProto).
    /// </summary>
    private static string? ReadTelegramMode(JsonElement root, List<ValidationError> errors)
    {
        if (!root.TryGetProperty("mode", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String
            || !TelegramSecretModes.IsKnown(value.GetString()))
        {
            errors.Add(new ValidationError("mode", "Способ входа должен быть bot или userbot"));

            return null;
        }

        return value.GetString();
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
}

/// <summary>Тело <c>TelegramSessionSecret</c> после проверки.</summary>
internal sealed record TelegramSessionInput(string Name, string Session, string? Mode, Guid? ProxyId);

/// <summary>Тело <c>ProxySecret</c> после проверки.</summary>
internal sealed record ProxyInput(
    string Name,
    string Scheme,
    string Host,
    int Port,
    string? Username,
    string? Password);

/// <summary>Тело <c>LlmSecret</c> после проверки.</summary>
internal sealed record LlmInput(string ApiKey, string BaseUrl, string Model, Guid? ProxyId);

/// <summary>Тело <c>SourceConnection</c> после проверки: <c>null</c> снимает привязку.</summary>
internal sealed record ConnectionInput(Guid? TelegramSessionId, Guid? ProxyId);
