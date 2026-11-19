using System.Text.Json;

namespace Ingestion.Pipeline;

/// <summary>
/// Разобранный ответ LLM. Поля ещё не проверены на соответствие источнику: код статуса
/// и фиксацию сверяет <c>ExtractionValidator</c>.
/// </summary>
/// <param name="Relevant">Относится ли сообщение к задаче промпта-фильтра.</param>
/// <param name="Status">Код статуса из ответа; <c>null</c>, если ответ нерелевантен.</param>
/// <param name="ObjectId">Идентификатор фиксации из ответа; <c>null</c>, если не определён.</param>
/// <param name="Confidence">Уверенность модели, 0..1; 0 для нерелевантного ответа.</param>
/// <param name="EvidenceQuote">Дословная цитата из сообщения; пустая для нерелевантного ответа.</param>
/// <param name="Address">Адрес из сообщения («улица дом»); <c>null</c>, если дом не назван.</param>
public sealed record ParsedLlmResponse(
    bool Relevant,
    string? Status,
    string? ObjectId,
    double Confidence,
    string EvidenceQuote,
    string? Address = null);

/// <summary>
/// Разбор сырого ответа LLM в <see cref="ParsedLlmResponse"/>. Принимает
/// чистый JSON и JSON в markdown-блоке, находит первый разбираемый JSON-объект среди лишнего
/// текста и возвращает <c>null</c> на любом несоответствии схеме. Исключений не бросает.
/// </summary>
public static class LlmResponseParser
{
    private const string RelevantField = "relevant";
    private const string StatusField = "status";
    private const string ObjectIdField = "object_id";
    private const string ConfidenceField = "confidence";
    private const string EvidenceQuoteField = "evidence_quote";
    private const string AddressField = "address";

    /// <summary>
    /// Разбирает ответ модели. Обязательные поля: <c>relevant</c>; при <c>relevant = true</c>
    /// ещё <c>status</c>, <c>confidence</c> и <c>evidence_quote</c>. <c>object_id</c> необязателен.
    /// </summary>
    /// <param name="raw">Сырой текст ответа; допускается <c>null</c>, пустая и очень длинная строка.</param>
    /// <returns>Разобранный ответ либо <c>null</c>, если JSON или схема неверны.</returns>
    public static ParsedLlmResponse? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var start = raw.IndexOf('{', StringComparison.Ordinal);
        while (start >= 0)
        {
            var end = FindObjectEnd(raw, start, out var unterminatedString);
            if (end > start)
            {
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(raw.AsMemory(start, end - start + 1));
                }
                catch (JsonException)
                {
                    start = raw.IndexOf('{', start + 1);
                    continue;
                }

                using (document)
                {
                    return Map(document.RootElement);
                }
            }

            if (unterminatedString)
            {
                // Строка так и не закрылась: все дальнейшие скобки — её содержимое.
                return null;
            }

            start = raw.IndexOf('{', start + 1);
        }

        return null;
    }

    /// <summary>
    /// Ищет индекс закрывающей фигурной скобки первого сбалансированного объекта, начиная с
    /// <paramref name="start"/>. Скобки внутри строк и экранированные кавычки не учитываются.
    /// </summary>
    /// <param name="text">Текст ответа модели.</param>
    /// <param name="start">Индекс открывающей фигурной скобки.</param>
    /// <param name="unterminatedString">
    /// Текст закончился внутри строкового литерала: искать объект дальше бессмысленно.
    /// </param>
    private static int FindObjectEnd(string text, int start, out bool unterminatedString)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                case '[':
                    depth++;
                    break;
                case '}':
                case ']':
                    depth--;
                    if (depth <= 0)
                    {
                        unterminatedString = false;
                        return c == '}' ? i : -1;
                    }

                    break;
                default:
                    break;
            }
        }

        unterminatedString = inString;
        return -1;
    }

    private static ParsedLlmResponse? Map(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!root.TryGetProperty(RelevantField, out var relevantElement) ||
            relevantElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        if (!relevantElement.GetBoolean())
        {
            // Для нерелевантного сообщения остальные поля не требуются.
            return new ParsedLlmResponse(false, null, null, 0d, string.Empty);
        }

        if (!TryGetTrimmedString(root, StatusField, out var status) || status.Length == 0)
        {
            return null;
        }

        if (!root.TryGetProperty(ConfidenceField, out var confidenceElement) ||
            confidenceElement.ValueKind != JsonValueKind.Number ||
            !confidenceElement.TryGetDouble(out var confidence) ||
            !double.IsFinite(confidence) ||
            confidence is < 0d or > 1d)
        {
            return null;
        }

        if (!root.TryGetProperty(EvidenceQuoteField, out var quoteElement) ||
            quoteElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? objectId = null;
        if (root.TryGetProperty(ObjectIdField, out var objectElement) &&
            objectElement.ValueKind != JsonValueKind.Null)
        {
            if (objectElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = objectElement.GetString()?.Trim();
            objectId = string.IsNullOrEmpty(value) ? null : value;
        }

        // Адрес второстепенен для статуса: неверный тип не отклоняет ответ, а просто отбрасывается.
        string? address = null;
        if (root.TryGetProperty(AddressField, out var addressElement) &&
            addressElement.ValueKind == JsonValueKind.String)
        {
            var value = addressElement.GetString()?.Trim();
            address = string.IsNullOrEmpty(value) ? null : value;
        }

        var quote = quoteElement.GetString() ?? string.Empty;
        return new ParsedLlmResponse(true, status, objectId, confidence, quote, address);
    }

    private static bool TryGetTrimmedString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString()?.Trim() ?? string.Empty;
        return true;
    }
}
