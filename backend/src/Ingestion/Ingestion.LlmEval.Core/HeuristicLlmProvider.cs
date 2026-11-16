using System.Text.Json;

using Ingestion.Abstractions;
using Ingestion.Pipeline;

namespace Ingestion.LlmEval.Core;

/// <summary>
/// Детерминированный fake-провайдер для <c>--provider fake</c>: ищет в сообщении подстроки
/// <c>fakeKeywords</c> из конфигурации источника и возвращает ответ в формате схемы.
/// </summary>
/// <remarks>
/// Это не модель и не оценка качества: fake нужен, чтобы проверить сам инструмент (CLI, метрики,
/// отчёты) без ключа LLM. Для содержательной оценки нужен <c>--provider openai</c> или другая
/// реальная модель. Если в конфигурации источника нет <c>fakeKeywords</c>, провайдер отвечает
/// <c>relevant: false</c>.
/// </remarks>
public sealed class HeuristicLlmProvider(EvalSourceConfig source) : ILlmProvider
{
    private const double Confidence = 0.9;

    private readonly EvalSourceConfig _source = source
        ?? throw new ArgumentNullException(nameof(source));

    /// <inheritdoc />
    public Task<string> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var text = ExtractMessage(request.Message);
        var status = FindStatus(text);

        if (status is null)
        {
            return Task.FromResult("""{"relevant":false}""");
        }

        var mapObjectId = FindObject(text);

        var response = JsonSerializer.Serialize(new
        {
            relevant = true,
            status = status.Value.Code,
            object_id = mapObjectId?.ToString("D"),
            confidence = Confidence,
            evidence_quote = status.Value.Quote,
        });

        return Task.FromResult(response);
    }

    private (string Code, string Quote)? FindStatus(string text)
    {
        (int Index, int Length, string Code, string Quote)? best = null;

        foreach (var status in _source.Statuses)
        {
            foreach (var keyword in status.FakeKeywords)
            {
                var index = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    continue;
                }

                if (best is null ||
                    index < best.Value.Index ||
                    (index == best.Value.Index && keyword.Length > best.Value.Length))
                {
                    best = (index, keyword.Length, status.Code, text.Substring(index, keyword.Length));
                }
            }
        }

        return best is null ? null : (best.Value.Code, best.Value.Quote);
    }

    private Guid? FindObject(string text)
    {
        if (!_source.LinkByText || _source.Objects.Count <= 1)
        {
            return null;
        }

        (int Index, int Length, string Code)? best = null;

        foreach (var mapObject in _source.Objects)
        {
            var candidates = new List<string>(mapObject.FakeKeywords.Count + 1)
            {
                mapObject.Name,
            };
            candidates.AddRange(mapObject.FakeKeywords);

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                var index = text.IndexOf(candidate, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    continue;
                }

                if (best is null ||
                    index < best.Value.Index ||
                    (index == best.Value.Index && candidate.Length > best.Value.Length))
                {
                    best = (index, candidate.Length, mapObject.Code);
                }
            }
        }

        return best is null ? null : _source.ObjectId(best.Value.Code);
    }

    private static string ExtractMessage(string message)
    {
        var start = message.IndexOf(PromptSafety.MessageStartMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return message.Trim();
        }

        var from = start + PromptSafety.MessageStartMarker.Length;
        var end = message.IndexOf(PromptSafety.MessageEndMarker, from, StringComparison.Ordinal);

        return (end < 0 ? message[from..] : message[from..end]).Trim();
    }
}
