using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Ingestion.LlmEval.Core;

/// <summary>
/// Сведения о прогоне, которые попадают в отчёт: модель, хеш промпта, дата и размер набора.
/// Ключ LLM и текст промпта сюда не входят.
/// </summary>
/// <param name="Provider">Провайдер: <c>openai</c> или <c>fake</c>.</param>
/// <param name="Model">Имя модели (для fake — подпись эвристики).</param>
/// <param name="PromptHash">SHA-256 системного и пользовательского промпта источника.</param>
/// <param name="DatasetPath">Путь к набору, как его указал пользователь.</param>
/// <param name="DatasetSize">Число случаев в наборе.</param>
/// <param name="SourceName">Название источника из конфигурации.</param>
/// <param name="SourceId">Идентификатор источника.</param>
/// <param name="LinkByText">Включён ли режим «определять фиксацию по тексту».</param>
/// <param name="ConfidenceThreshold">Порог confidence источника.</param>
/// <param name="StatusCodes">Коды статусов источника.</param>
/// <param name="ObjectCodes">Коды фиксаций источника.</param>
public sealed record EvalRunInfo(
    string Provider,
    string Model,
    string PromptHash,
    string DatasetPath,
    int DatasetSize,
    string SourceName,
    Guid SourceId,
    bool LinkByText,
    double ConfidenceThreshold,
    IReadOnlyList<string> StatusCodes,
    IReadOnlyList<string> ObjectCodes);

/// <summary>Результат одного случая в отчёте: ожидание, решение и признаки ошибок.</summary>
/// <param name="Id">Идентификатор случая.</param>
/// <param name="Excerpt">Первые 300 символов текста (пробелы схлопнуты) — для таблицы ошибок.</param>
/// <param name="Expected">Ожидание разметчика.</param>
/// <param name="Decision">Вид решения: <c>Propose</c>, <c>Moderate</c> или <c>Drop</c>.</param>
/// <param name="Proposed">Опубликованные предложения (для Propose).</param>
/// <param name="ProposedStatusCode">Статус из карточки модерации.</param>
/// <param name="ProposedObjectCode">Фиксация из карточки модерации.</param>
/// <param name="ModerationReason">Причина модерации в терминах метрик.</param>
/// <param name="DropReason">Причина отброса в терминах метрик.</param>
/// <param name="StatusCorrect">Совпал ли статус.</param>
/// <param name="ObjectCorrect">Совпала ли фиксация.</param>
/// <param name="Correct">Верно ли решение по случаю целиком.</param>
/// <param name="Note">Комментарий разметчика.</param>
public sealed record EvalCaseReport(
    string Id,
    string Excerpt,
    EvalExpectation Expected,
    string Decision,
    IReadOnlyList<EvalProposedObservation> Proposed,
    string? ProposedStatusCode,
    string? ProposedObjectCode,
    string? ModerationReason,
    string? DropReason,
    bool StatusCorrect,
    bool ObjectCorrect,
    bool Correct,
    string? Note);

/// <summary>
/// Отчёт о прогоне размеченного набора: метаданные, метрики и результаты
/// по случаям. Пишется в Markdown и JSON; в отчёте нет ключа LLM и текста системного промпта —
/// только имя модели и хеш промпта, чтобы прогоны можно было сравнивать в регрессионном режиме.
/// </summary>
/// <param name="SchemaVersion">Версия формата JSON; baseline обязан совпадать.</param>
/// <param name="GeneratedAt">Дата и время формирования отчёта (UTC).</param>
/// <param name="Run">Параметры прогона.</param>
/// <param name="Metrics">Метрики /.</param>
/// <param name="Cases">Результаты по случаям.</param>
public sealed record EvalReport(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    EvalRunInfo Run,
    EvalMetrics Metrics,
    IReadOnlyList<EvalCaseReport> Cases)
{
    /// <summary>Текущая версия формата JSON-отчёта.</summary>
    public const string CurrentSchemaVersion = "1.0";

    private const int ExcerptLength = 300;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Собирает отчёт из результата прогона и сведений о нём.</summary>
    /// <param name="run">Результат прогона.</param>
    /// <param name="info">Метаданные прогона.</param>
    /// <param name="generatedAt">Дата формирования отчёта (UTC).</param>
    public static EvalReport Create(
        EvalRunResult run,
        EvalRunInfo info,
        DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(info);

        var cases = run.Cases
            .Select(item => new EvalCaseReport(
                item.Case.Id,
                Excerpt(item.Case.Text),
                item.Case.Expected,
                item.Decision.ToString(),
                item.Proposed,
                item.ProposedStatusCode,
                item.ProposedObjectCode,
                item.ModerationReason,
                item.DropReason,
                item.StatusCorrect,
                item.ObjectCorrect,
                item.Correct,
                item.Case.Note))
            .ToArray();

        return new EvalReport(
            CurrentSchemaVersion,
            generatedAt.ToUniversalTime(),
            info,
            run.Metrics,
            cases);
    }

    /// <summary>Сериализует отчёт в JSON (camelCase, без экранирования кириллицы).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Формирует Markdown-отчёт с метриками, распределением модерации и ошибками.</summary>
    /// <param name="comparison">Результат сравнения с baseline; <c>null</c> — обычный прогон.</param>
    public string ToMarkdown(BaselineComparison? comparison = null)
    {
        var text = new StringBuilder();

        text.AppendLine("# Оценка качества LLM ");
        text.AppendLine();

        text.AppendLine($"- **Дата (UTC):** {GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
        text.AppendLine($"- **Провайдер:** {Run.Provider}");
        text.AppendLine($"- **Модель:** {Run.Model}");
        text.AppendLine($"- **Хеш промпта:** `{Run.PromptHash}`");
        text.AppendLine($"- **Набор:** `{Run.DatasetPath}` — {CasesText(Run.DatasetSize)}");
        text.AppendLine($"- **Источник:** {Run.SourceName} (`{Run.SourceId:D}`)");
        text.AppendLine($"- **Режим «по тексту»:** {(Run.LinkByText ? "включён" : "выключен")}");
        text.AppendLine($"- **Порог confidence:** {Run.ConfidenceThreshold.ToString("0.00", CultureInfo.InvariantCulture)}");
        text.AppendLine($"- **Статусы:** {Codes(Run.StatusCodes)}");
        text.AppendLine($"- **Фиксации:** {Codes(Run.ObjectCodes)}");
        text.AppendLine();

        AppendMetrics(text);
        AppendComparison(text, comparison);
        AppendFormulas(text);
        AppendModeration(text);
        AppendDrops(text);
        AppendMatrix(text);
        AppendErrors(text);

        text.AppendLine("> В отчёте нет ключа LLM и текста системного промпта: для сравнения прогонов");
        text.AppendLine("> достаточно модели и хеша промпта.");

        return text.ToString();
    }

    private void AppendMetrics(StringBuilder text)
    {
        var metrics = Metrics;

        text.AppendLine("## Метрики");
        text.AppendLine();
        text.AppendLine("| Метрика | Значение | Цель |");
        text.AppendLine("|---|---|---|");
        text.AppendLine(
            $"| Precision опубликованных наблюдений | {RatioWithCounts(metrics.Precision, metrics.PublishedCorrect, metrics.PublishedCount)} | ≥ {EvalMetrics.TargetPrecision.ToString("0.00", CultureInfo.InvariantCulture)} |");
        text.AppendLine(
            $"| Recall экстрактора | {RatioWithCounts(metrics.ExtractorRecall, metrics.ExtractorHits, metrics.ExtractorTotal)} | ≥ {EvalMetrics.TargetRecall.ToString("0.00", CultureInfo.InvariantCulture)} |");
        text.AppendLine(
            $"| Recall линкера | {LinkerValue(metrics)} | ≥ {EvalMetrics.TargetRecall.ToString("0.00", CultureInfo.InvariantCulture)} |");
        text.AppendLine(
            $"| Случаи | {metrics.CaseCount.ToString(CultureInfo.InvariantCulture)} (релевантных {metrics.RelevantCount.ToString(CultureInfo.InvariantCulture)}, нерелевантных {metrics.NotRelevantCount.ToString(CultureInfo.InvariantCulture)}) | — |");
        text.AppendLine(
            $"| Ушло в модерацию | {metrics.ModerationCount.ToString(CultureInfo.InvariantCulture)} из {metrics.CaseCount.ToString(CultureInfo.InvariantCulture)} ({Percent(metrics.ModerationCount, metrics.CaseCount)}%) | — |");
        text.AppendLine(
            $"| Отброшено | {metrics.DropCount.ToString(CultureInfo.InvariantCulture)} из {metrics.CaseCount.ToString(CultureInfo.InvariantCulture)} ({Percent(metrics.DropCount, metrics.CaseCount)}%) | — |");
        text.AppendLine();
    }

    private static void AppendComparison(StringBuilder text, BaselineComparison? comparison)
    {
        if (comparison is null)
        {
            return;
        }

        text.AppendLine("## Регрессия");
        text.AppendLine();
        text.AppendLine($"- **Baseline precision:** {Value(comparison.BaselinePrecision)}" +
                        (comparison.BaselineModel is null ? string.Empty : $" (модель: {comparison.BaselineModel})"));
        text.AppendLine($"- **Текущая precision:** {Value(comparison.CurrentPrecision)}");
        text.AppendLine(
            $"- **Просадка:** {comparison.DropPercentagePoints.ToString("0.00", CultureInfo.InvariantCulture)} п.п. (допустимо не более {EvalBaseline.MaxDropPercentagePoints.ToString("0.00", CultureInfo.InvariantCulture)} п.п.)");
        text.AppendLine(
            $"- **Минимальный precision:** {comparison.MinPrecision.ToString("0.000", CultureInfo.InvariantCulture)}");
        text.AppendLine($"- **Результат:** {(comparison.Failed ? "ПРОВАЛ" : "OK")} — {comparison.Summary}");
        text.AppendLine();
    }

    private static void AppendFormulas(StringBuilder text)
    {
        text.AppendLine("## Как считаются метрики");
        text.AppendLine();
        text.AppendLine("- **Precision опубликованных наблюдений** = верные опубликованные наблюдения / все опубликованные наблюдения.");
        text.AppendLine("  Единица — наблюдение: при выключенном режиме «по тексту» одно сообщение публикует статус на все фиксации источника,");
        text.AppendLine("  и каждая такая публикация входит в знаменатель. Верное наблюдение совпадает и по статусу, и по фиксации");
        text.AppendLine("  (если разметчик фиксацию указал).");
        text.AppendLine("- **Recall экстрактора** = случаи с верным статусом / случаи, размеченные как релевантные и имеющие статус.");
        text.AppendLine("  Засчитывается и публикация без модерации, и карточка модерации, в которой предложен верный статус:");
        text.AppendLine("  метрика отвечает на вопрос «извлекла ли модель правильный статус», а не «попал ли он на карту».");
        text.AppendLine("- **Recall линкера** = случаи с верно найденной фиксацией / релевантные случаи с размеченной фиксацией.");
        text.AppendLine("  Считается только в режиме «по тексту» при нескольких фиксациях: при выключенном режиме фиксация не определяется,");
        text.AppendLine("  а при одной фиксации выбора нет.");
        text.AppendLine();
    }

    private void AppendModeration(StringBuilder text)
    {
        text.AppendLine("## Модерация по причинам");
        text.AppendLine();
        text.AppendLine("| Причина | Случаев | Доля от набора |");
        text.AppendLine("|---|---|---|");

        foreach (var reason in EvalMetrics.ModerationReasons)
        {
            var count = Metrics.ModerationByReason.TryGetValue(reason, out var value) ? value : 0;
            text.AppendLine(
                $"| `{reason}` | {count.ToString(CultureInfo.InvariantCulture)} | {Percent(count, Metrics.CaseCount)}% |");
        }

        text.AppendLine();
        text.AppendLine(
            "`llm_error` — сбой провайдера; в БД он хранится как `invalid_response` (check-констрейнт схемы),");
        text.AppendLine("в отчёте выделен отдельно для диагностики. «Цитата не подстрока» также попадает в `invalid_response`.");
        text.AppendLine();
    }

    private void AppendDrops(StringBuilder text)
    {
        text.AppendLine("## Отброшенные сообщения");
        text.AppendLine();
        text.AppendLine("| Причина | Случаев | Доля от набора |");
        text.AppendLine("|---|---|---|");

        foreach (var reason in EvalMetrics.DropReasons)
        {
            var count = Metrics.DropByReason.TryGetValue(reason, out var value) ? value : 0;
            text.AppendLine(
                $"| `{reason}` | {count.ToString(CultureInfo.InvariantCulture)} | {Percent(count, Metrics.CaseCount)}% |");
        }

        text.AppendLine();
    }

    private void AppendMatrix(StringBuilder text)
    {
        var matrix = Metrics.StatusMatrix;

        text.AppendLine("## Матрица ошибок по статусам");
        text.AppendLine();
        text.Append("| Ожидаемый \\ фактический |");

        foreach (var column in matrix.ActualColumns)
        {
            text.Append($" {Cell(column)} |");
        }

        text.AppendLine();
        text.Append("|---|");
        foreach (var _ in matrix.ActualColumns)
        {
            text.Append("---|");
        }

        text.AppendLine();

        for (var row = 0; row < matrix.ExpectedRows.Count; row++)
        {
            text.Append($"| {Cell(matrix.ExpectedRows[row])} |");

            var cells = matrix.Cells[row];
            for (var column = 0; column < cells.Count; column++)
            {
                text.Append($" {cells[column].ToString(CultureInfo.InvariantCulture)} |");
            }

            text.AppendLine();
        }

        text.AppendLine();
    }

    private void AppendErrors(StringBuilder text)
    {
        var errors = Cases.Where(item => !item.Correct).ToArray();

        text.AppendLine($"## Ошибки ({errors.Length.ToString(CultureInfo.InvariantCulture)})");
        text.AppendLine();

        if (errors.Length == 0)
        {
            text.AppendLine("Все случаи совпали с разметкой.");
            text.AppendLine();
            return;
        }

        text.AppendLine("| Случай | Текст (фрагмент) | Ожидание | Факт |");
        text.AppendLine("|---|---|---|---|");

        const int maxRows = 100;
        foreach (var item in errors.Take(maxRows))
        {
            text.AppendLine(
                $"| `{Cell(item.Id)}` | {Cell(item.Excerpt)} | {Cell(Expectation(item.Expected))} | {Cell(Actual(item))} |");
        }

        if (errors.Length > maxRows)
        {
            text.AppendLine();
            text.AppendLine(
                $"Показаны первые {maxRows.ToString(CultureInfo.InvariantCulture)} из {errors.Length.ToString(CultureInfo.InvariantCulture)} ошибок; полный список — в JSON-отчёте.");
        }

        text.AppendLine();
    }

    private static string Actual(EvalCaseReport item) => item.Decision switch
    {
        "Propose" => item.Proposed.Count == 0
            ? "Propose без наблюдений"
            : "Propose: " + string.Join(
                ", ",
                item.Proposed.Select(observation =>
                    $"{observation.StatusCode}/{observation.ObjectCode ?? "—"}")),
        "Moderate" => $"Moderate ({item.ModerationReason}): {item.ProposedStatusCode ?? "—"}/{item.ProposedObjectCode ?? "—"}",
        "Drop" => $"Drop ({item.DropReason})",
        _ => item.Decision,
    };

    private static string Expectation(EvalExpectation expected) =>
        expected.Relevant
            ? $"relevant: true; status: {expected.Status ?? "—"}; objectCode: {expected.ObjectCode ?? "—"}"
            : "relevant: false";

    private static string LinkerValue(EvalMetrics metrics)
    {
        if (!metrics.LinkerApplicable)
        {
            return "n/a (режим «по тексту» выключен или одна фиксация)";
        }

        return metrics.LinkerTotal == 0
            ? "n/a (нет релевантных случаев с размеченной фиксацией)"
            : RatioWithCounts(metrics.LinkerRecall, metrics.LinkerHits, metrics.LinkerTotal);
    }

    private static string RatioWithCounts(double? value, int hits, int total) =>
        $"{EvalMetrics.FormatRatio(value)} ({hits.ToString(CultureInfo.InvariantCulture)} из {total.ToString(CultureInfo.InvariantCulture)})";

    private static string CasesText(int count)
    {
        var lastTwo = count % 100;
        var last = count % 10;

        var word = lastTwo is >= 11 and <= 14
            ? "случаев"
            : last switch
            {
                1 => "случай",
                2 or 3 or 4 => "случая",
                _ => "случаев",
            };

        return $"{count.ToString(CultureInfo.InvariantCulture)} {word}";
    }

    private static string Percent(int count, int total) =>
        EvalMetrics.Percent(count, total).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Value(double? value) =>
        value is null ? "n/a" : value.Value.ToString("0.000", CultureInfo.InvariantCulture);

    private static string Codes(IReadOnlyList<string> codes) =>
        codes.Count == 0 ? "—" : string.Join(", ", codes.Select(code => $"`{code}`"));

    private static string Cell(string? value) =>
        (value ?? string.Empty)
        .Replace("|", "\\|", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    private static string Excerpt(string text)
    {
        var collapsed = string.Join(
            ' ',
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Length <= ExcerptLength
            ? collapsed
            : collapsed[..ExcerptLength] + "…";
    }
}

/// <summary>
/// Запись отчёта: Markdown и JSON пишутся рядом (по расширению <c>--out</c> или в оба файла
/// с общим базовым именем). Каталоги создаются автоматически.
/// </summary>
public static class EvalReportWriter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Пишет Markdown- и JSON-отчёт по указанным путям.</summary>
    /// <param name="report">Отчёт.</param>
    /// <param name="markdownPath">Путь к <c>.md</c>.</param>
    /// <param name="jsonPath">Путь к <c>.json</c>.</param>
    /// <param name="comparison">Результат сравнения с baseline для раздела «Регрессия».</param>
    /// <param name="ct">Токен отмены.</param>
    public static async Task WriteAsync(
        EvalReport report,
        string markdownPath,
        string jsonPath,
        BaselineComparison? comparison,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);

        await WriteFileAsync(markdownPath, report.ToMarkdown(comparison), ct).ConfigureAwait(false);
        await WriteFileAsync(jsonPath, report.ToJson(), ct).ConfigureAwait(false);
    }

    private static async Task WriteFileAsync(string path, string content, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, content, Utf8WithoutBom, ct).ConfigureAwait(false);
    }
}
