using System.Text.RegularExpressions;

namespace Ingestion.Pipeline;

/// <summary>
/// Настройки префильтра источника (шаг 1). Оба списка необязательны; если заданы оба,
/// достаточно совпадения любого правила.
/// </summary>
/// <param name="Keywords">Ключевые слова; совпадение без учёта регистра по подстроке.</param>
/// <param name="Regexes">Регулярные выражения .NET; сравнение с таймаутом против ReDoS.</param>
public sealed record PrefilterConfig(
    IReadOnlyList<string>? Keywords,
    IReadOnlyList<string>? Regexes);

/// <summary>
/// Дешёвый префильтр сообщений (шаг 1): снижает расход LLM, отсеивая заведомо
/// нерелевантные сообщения до запроса. Главный фильтр — промпт пользователя, поэтому
/// отсутствие конфигурации и пустые списки пропускают всё.
/// </summary>
/// <remarks>
/// Регулярные выражения — недоверенная конфигурация: невалидный шаблон и таймаут выполнения
/// считаются «не совпало». Исключение наружу не выходит, воркер не падает и не зависает
/// на катастрофическом шаблоне (ReDoS).
/// </remarks>
public static class Prefilter
{
    /// <summary>Таймаут одного регулярного выражения; по истечении совпадение считается ложным.</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Проверяет, проходит ли текст префильтр. Без конфигурации, с пустыми списками или
    /// с пустыми правилами — проходит всё.
    /// </summary>
    /// <param name="text">Текст сообщения.</param>
    /// <param name="config">Настройки префильтра источника; <c>null</c> — фильтра нет.</param>
    /// <returns><c>true</c>, если сообщение стоит отправлять в LLM.</returns>
    public static bool Passes(string text, PrefilterConfig? config)
    {
        if (config is null)
        {
            return true;
        }

        var value = text ?? string.Empty;
        var keywords = config.Keywords;
        var regexes = config.Regexes;
        var hasKeywords = keywords is { Count: > 0 };
        var hasRegexes = regexes is { Count: > 0 };

        if (!hasKeywords && !hasRegexes)
        {
            return true;
        }

        if (hasKeywords)
        {
            foreach (var keyword in keywords!)
            {
                // Пустое слово совпало бы с любым текстом и обесценило фильтр: пропускаем его.
                if (string.IsNullOrEmpty(keyword))
                {
                    continue;
                }

                if (value.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        if (hasRegexes)
        {
            foreach (var pattern in regexes!)
            {
                if (Matches(pattern, value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Проверяет одно регулярное выражение. Невалидный шаблон и таймаут — «не совпало»,
    /// без исключения и без ожидания дольше <see cref="RegexTimeout"/>.
    /// </summary>
    private static bool Matches(string? pattern, string text)
    {
        // Пустой шаблон совпадает с чем угодно и ничего не фильтрует: считаем его отсутствующим.
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        try
        {
            var regex = new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // RegexParseException — невалидный шаблон из настроек источника.
            return false;
        }
    }
}
