using System.Text.Json;

using Microsoft.AspNetCore.Http;

namespace BuildingBlocks.Web;

/// <summary>
/// Ответы об ошибках по RFC 7807 (<c>application/problem+json</c>). Нужны middleware, у которого
/// нет доступа к minimal API-хелперам: аутентификация, авторизация, общий обработчик исключений.
/// </summary>
public static partial class ProblemResponses
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Записывает Problem Details в ответ, если он ещё не начат.</summary>
    public static async Task WriteAsync(
        HttpResponse response,
        int statusCode,
        string title,
        string detail,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.HasStarted)
        {
            return;
        }

        response.Clear();
        response.StatusCode = statusCode;
        response.ContentType = "application/problem+json";

        var body = new ProblemBody("about:blank", title, statusCode, detail);
        await JsonSerializer.SerializeAsync(response.Body, body, JsonOptions, cancellationToken);
    }

    private sealed record ProblemBody(string Type, string Title, int Status, string Detail);
}
