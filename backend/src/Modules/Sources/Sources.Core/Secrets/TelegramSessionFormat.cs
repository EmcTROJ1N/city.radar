using System.Globalization;
using System.Text.Json;

namespace Sources.Core.Secrets;

/// <summary>Способы входа Telegram-сессии: токен бота и аккаунт MTProto.</summary>
internal static class TelegramSecretModes
{
    public const string Bot = "bot";
    public const string Userbot = "userbot";

    public static bool IsKnown(string? mode) =>
        string.Equals(mode, Bot, StringComparison.Ordinal)
        || string.Equals(mode, Userbot, StringComparison.Ordinal);
}

/// <summary>
/// Разбор и проверка значения секрета Telegram-сессии. Токен бота — обычная строка,
/// MTProto-сессия — JSON <c>{apiId, apiHash, sessionData, phone?}</c> (допускаются
/// snake_case-ключи, как в Worker-е).
/// </summary>
internal static class TelegramSessionFormat
{
    /// <summary>Способ входа по сохранённому значению; неизвестный формат считается ботом.</summary>
    public static string Detect(string session) =>
        LooksLikeMtProto(session) ? TelegramSecretModes.Userbot : TelegramSecretModes.Bot;

    /// <summary>Похоже ли значение на JSON-сессию MTProto.</summary>
    public static bool LooksLikeMtProto(string session)
    {
        if (string.IsNullOrWhiteSpace(session) || session.TrimStart()[0] != '{')
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(session);
            var root = document.RootElement;

            return root.ValueKind == JsonValueKind.Object
                && (root.TryGetProperty("apiId", out _)
                    || root.TryGetProperty("api_id", out _)
                    || root.TryGetProperty("apiHash", out _)
                    || root.TryGetProperty("api_hash", out _));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Проверяет JSON-сессию MTProto. Возвращает текст ошибки для поля <c>session</c>
    /// или <c>null</c>, если сессия пригодна для Worker-а.
    /// </summary>
    public static string? ValidateMtProto(string session)
    {
        if (string.IsNullOrWhiteSpace(session))
        {
            return "Укажите строку сессии MTProto.";
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(session);
        }
        catch (JsonException)
        {
            return "Сессия MTProto должна быть JSON-объектом "
                + "{apiId, apiHash, sessionData, phone?}.";
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return "Сессия MTProto должна быть JSON-объектом "
                    + "{apiId, apiHash, sessionData, phone?}.";
            }

            if (!TryReadApiId(root, out var apiId) || apiId <= 0)
            {
                return "В сессии MTProto не задан числовой apiId.";
            }

            var apiHash = ReadString(root, "apiHash") ?? ReadString(root, "api_hash");
            if (string.IsNullOrWhiteSpace(apiHash))
            {
                return "В сессии MTProto не задан apiHash.";
            }

            var sessionData = ReadString(root, "sessionData") ?? ReadString(root, "session_data");
            if (string.IsNullOrWhiteSpace(sessionData))
            {
                return "В сессии MTProto нет sessionData: выполните вход в аккаунт "
                    + "и сохраните строку сессии.";
            }

            return null;
        }
    }

    private static bool TryReadApiId(JsonElement root, out int apiId)
    {
        apiId = 0;

        if (!root.TryGetProperty("apiId", out var value)
            && !root.TryGetProperty("api_id", out value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out apiId),
            JsonValueKind.String => int.TryParse(
                value.GetString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out apiId),
            _ => false,
        };
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
