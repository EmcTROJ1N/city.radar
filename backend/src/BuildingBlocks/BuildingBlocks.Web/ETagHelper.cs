using System.Security.Cryptography;
using System.Text;

namespace BuildingBlocks.Web;

/// <summary>
/// Слабые ETag для условных запросов (RFC 9110, §8.8.3).
/// </summary>
/// <remarks>
/// Значение тега — усечённый SHA-256 в base64url (22 символа, 128 бит), поэтому ETag
/// детерминирован для одинакового содержимого и не раскрывает исходные данные.
/// Все теги слабые (<c>W/"…"</c>): побайтовое совпадение представления не требуется,
/// достаточно совпадения версии данных и нормализованных параметров запроса.
/// Сравнение идёт по weak-правилам RFC: префикс <c>W/</c> игнорируется, сравнивается
/// только opaque-tag внутри кавычек; <c>*</c> совпадает с любым текущим представлением.
/// </remarks>
public static class ETagHelper
{
    /// <summary>
    /// Значение <c>Cache-Control</c> для ответов карты: ответ можно хранить, но перед каждым
    /// использованием обязательна проверка через <c>If-None-Match</c>. Это даёт дешёвый 304
    /// при неизменных данных и гарантирует, что клиент не покажет устаревшую карту.
    /// </summary>
    public const string CacheControl = "no-cache";

    /// <summary>Длина усечённого хеша: 16 байт (128 бит) → 22 символа base64url.</summary>
    private const int HashBytes = 16;

    /// <summary>Создаёт слабый ETag из произвольной строки.</summary>
    /// <param name="content">Содержимое, однозначно определяющее представление.</param>
    /// <returns>Тег вида <c>W/"…"</c>.</returns>
    public static string CreateWeak(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        return CreateWeakTag(ComputeHash(content));
    }

    /// <summary>
    /// Создаёт слабый ETag ответа из версии данных и нормализованного запроса.
    /// </summary>
    /// <param name="version">Версия состояния карты (см. <c>MapVersionProvider</c>).</param>
    /// <param name="normalizedQuery">Нормализованные параметры запроса.</param>
    /// <returns>Тег вида <c>W/"…"</c>.</returns>
    public static string CreateWeak(string version, string normalizedQuery)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(normalizedQuery);

        return CreateWeakTag(ComputeHash(version + "\n" + normalizedQuery));
    }

    /// <summary>
    /// Усечённый SHA-256 в base64url: детерминированный идентификатор содержимого.
    /// </summary>
    /// <param name="value">Строка для хеширования.</param>
    /// <returns>22 символа <c>A–Z a–z 0–9 - _</c>.</returns>
    public static string ComputeHash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));

        return Convert.ToBase64String(hash, 0, HashBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// Проверяет, соответствует ли заголовок <c>If-None-Match</c> текущему ETag по
    /// weak-правилам: список значений через запятую, необязательный префикс <c>W/</c>,
    /// звёздочка <c>*</c> совпадает всегда.
    /// </summary>
    /// <param name="ifNoneMatch">Сырое значение заголовка; <c>null</c>/пусто — не совпадает.</param>
    /// <param name="etag">Текущий ETag ответа.</param>
    public static bool Matches(string? ifNoneMatch, string etag)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatch) || string.IsNullOrWhiteSpace(etag))
        {
            return false;
        }

        var current = OpaqueTag(etag);
        if (current is null)
        {
            return false;
        }

        foreach (var candidate in EnumerateTags(ifNoneMatch))
        {
            if (candidate == "*")
            {
                return true;
            }

            // Weak comparison: W/ у кандидата и у текущего тега не учитывается.
            if (string.Equals(OpaqueTag(candidate), current, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Разбирает заголовок <c>If-None-Match</c> на отдельные значения. Нужен для тестов и
    /// диагностики; сравнение выполняет <see cref="Matches"/>.
    /// </summary>
    /// <param name="ifNoneMatch">Сырое значение заголовка.</param>
    public static IReadOnlyList<string> Parse(string? ifNoneMatch)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatch))
        {
            return [];
        }

        return [.. EnumerateTags(ifNoneMatch)];
    }

    private static string CreateWeakTag(string hash) => $"W/\"{hash}\"";

    /// <summary>
    /// Делит список тегов по запятым, не разрывая содержимое кавычек: opaque-tag формально
    /// может содержать запятую.
    /// </summary>
    private static IEnumerable<string> EnumerateTags(string header)
    {
        var start = 0;
        var inQuotes = false;

        for (var index = 0; index < header.Length; index++)
        {
            var character = header[index];

            if (character == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (character == ',' && !inQuotes)
            {
                var value = header[start..index].Trim();
                if (value.Length > 0)
                {
                    yield return value;
                }

                start = index + 1;
            }
        }

        var tail = header[start..].Trim();
        if (tail.Length > 0)
        {
            yield return tail;
        }
    }

    /// <summary>
    /// Возвращает opaque-tag без слабого префикса и кавычек; <c>null</c> — значение невалидно.
    /// </summary>
    private static string? OpaqueTag(string value)
    {
        var tag = value.Trim();

        if (tag.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            tag = tag[2..].TrimStart();
        }

        if (tag.Length < 2 || tag[0] != '"' || tag[^1] != '"')
        {
            return null;
        }

        return tag[1..^1];
    }
}
