using System.Text.Json;

using Microsoft.AspNetCore.Http;

namespace IdentityAccess.Core.Admin;

/// <summary>
/// Разбор JSON-тела запроса админских эндпоинтов. Тело читается как <see cref="JsonDocument"/>,
/// чтобы строго проверить набор полей (<c>additionalProperties: false</c>) и отдать 422 с
/// <c>errors[field,message]</c>, как в контракте.
/// </summary>
internal static class AdminRequestJson
{
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
}
