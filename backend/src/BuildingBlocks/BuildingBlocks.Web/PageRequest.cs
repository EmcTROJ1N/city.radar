using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace BuildingBlocks.Web;

/// <summary>
/// Параметры курсорной страницы из контракта (<c>components/parameters/Limit</c> и
/// <c>Cursor</c>): <c>limit</c> от 1 до 100 (по умолчанию 20) и непрозрачный курсор из
/// <c>nextCursor</c> предыдущей страницы. Неверные значения — 400 по RFC 7807.
/// </summary>
public sealed class PageRequest
{
    /// <summary>Размер страницы по умолчанию (контракт: <c>default: 20</c>).</summary>
    public const int DefaultLimit = 20;

    /// <summary>Минимальный размер страницы (контракт: <c>minimum: 1</c>).</summary>
    public const int MinLimit = 1;

    /// <summary>Максимальный размер страницы (контракт: <c>maximum: 100</c>).</summary>
    public const int MaxLimit = 100;

    private PageRequest(int limit, string? cursorPayload)
    {
        Limit = limit;
        CursorPayload = cursorPayload;
    }

    /// <summary>Размер запрошенной страницы.</summary>
    public int Limit { get; }

    /// <summary>
    /// Полезная нагрузка курсора после декодирования; <c>null</c> — запрошена первая страница.
    /// Формат нагрузки задаёт эндпоинт: общая часть проверяет только её целостность.
    /// </summary>
    public string? CursorPayload { get; }

    /// <summary>Запрошена страница после курсора, а не первая.</summary>
    public bool HasCursor => CursorPayload is not null;

    /// <summary>
    /// Разбирает <c>limit</c> и <c>cursor</c>. При ошибке возвращает <c>false</c>, а
    /// <paramref name="failure"/> содержит готовый ответ 400 <c>application/problem+json</c>.
    /// </summary>
    /// <param name="limit">Значение query-параметра <c>limit</c>.</param>
    /// <param name="cursor">Значение query-параметра <c>cursor</c>.</param>
    /// <param name="request">Разобранные параметры страницы.</param>
    /// <param name="failure">Ответ с ошибкой или <c>null</c> при успехе.</param>
    public static bool TryCreate(
        int? limit,
        string? cursor,
        out PageRequest request,
        [NotNullWhen(false)] out IResult? failure)
    {
        failure = null;

        var pageSize = limit ?? DefaultLimit;
        if (pageSize is < MinLimit or > MaxLimit)
        {
            request = new PageRequest(DefaultLimit, null);
            failure = ProblemResponses.BadRequest(
                $"Параметр limit должен быть от {MinLimit} до {MaxLimit}");

            return false;
        }

        if (string.IsNullOrWhiteSpace(cursor))
        {
            request = new PageRequest(pageSize, null);

            return true;
        }

        if (!CursorCodec.TryDecode(cursor, out var payload))
        {
            request = new PageRequest(pageSize, null);
            failure = ProblemResponses.BadRequest("Некорректный курсор");

            return false;
        }

        request = new PageRequest(pageSize, payload);

        return true;
    }

    /// <summary>Кодирует позицию (например, <c>maxUserId</c>) в непрозрачный курсор.</summary>
    public static string EncodeCursor(long position) =>
        CursorCodec.Encode(position.ToString(CultureInfo.InvariantCulture));

    /// <summary>Кодирует составную позицию «тики UTC:id» (например, время и id записи аудита).</summary>
    public static string EncodeCursor(long ticks, long id) =>
        CursorCodec.Encode(
            $"{ticks.ToString(CultureInfo.InvariantCulture)}:{id.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>Кодирует произвольную полезную нагрузку в непрозрачный курсор.</summary>
    public static string EncodeCursor(string payload) => CursorCodec.Encode(payload);
}

/// <summary>
/// Непрозрачный курсор пагинации: контракт описывает его как строку из <c>nextCursor</c>
/// предыдущей страницы и не задаёт формат, поэтому внутри — base64url от полезной нагрузки.
/// </summary>
internal static class CursorCodec
{
    public static string Encode(string payload) =>
        WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(payload));

    public static bool TryDecode(string? cursor, out string payload)
    {
        payload = string.Empty;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            payload = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cursor));

            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
