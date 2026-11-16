using System.Globalization;

namespace Ingestion.LlmEval.Core;

/// <summary>
/// Матрица ошибок «ожидаемый статус × предложенный статус»: позволяет увидеть,
/// какие статусы модель путает, а не только итоговые precision/recall.
/// </summary>
/// <param name="ExpectedRows">Подписи строк в порядке вывода.</param>
/// <param name="ActualColumns">Подписи столбцов в порядке вывода.</param>
/// <param name="Cells">Значения: <c>Cells[row][column]</c>.</param>
public sealed record EvalStatusMatrix(
    IReadOnlyList<string> ExpectedRows,
    IReadOnlyList<string> ActualColumns,
    IReadOnlyList<IReadOnlyList<int>> Cells)
{
    /// <summary>Столбец «модель не предложила статус» — отброс или модерация без статуса.</summary>
    public const string NoStatusColumn = "(нет предложения)";

    /// <summary>Строка для сообщений, размеченных как нерелевантные.</summary>
    public const string NotRelevantRow = "(нерелевантно)";

    /// <summary>Строка для релевантных сообщений без однозначного статуса в разметке.</summary>
    public const string NoExpectedStatusRow = "(без статуса)";
}

/// <summary>
/// Метрики качества LLM-разбора. Единицы измерения:
/// <list type="bullet">
/// <item><description>precision — опубликованное наблюдение (а не сообщение): при режиме «ко всем фиксациям» одно сообщение даёт несколько наблюдений;</description></item>
/// <item><description>recall экстрактора — сообщение, размеченное как релевантное и имеющее статус;</description></item>
/// <item><description>recall линкера — сообщение в режиме «определять фиксацию по тексту» при нескольких фиксациях.</description></item>
/// </list>
/// </summary>
/// <param name="CaseCount">Всего случаев в наборе.</param>
/// <param name="RelevantCount">Случаев, размеченных как релевантные.</param>
/// <param name="NotRelevantCount">Случаев, размеченных как нерелевантные.</param>
/// <param name="PublishedCount">Опубликованных без модерации наблюдений (знаменатель precision).</param>
/// <param name="PublishedCorrect">Опубликованных наблюдений с верным статусом и верной фиксацией.</param>
/// <param name="Precision">Доля верных среди опубликованных; <c>null</c>, если публикаций не было.</param>
/// <param name="ExtractorTotal">Случаев в знаменателе recall экстрактора.</param>
/// <param name="ExtractorHits">Из них с верным предложенным статусом (Propose или Moderate).</param>
/// <param name="ExtractorRecall">Recall экстрактора; <c>null</c>, если знаменатель пуст.</param>
/// <param name="LinkerApplicable">Применим ли recall линкера (режим «по тексту» и больше одной фиксации).</param>
/// <param name="LinkerTotal">Случаев в знаменателе recall линкера.</param>
/// <param name="LinkerHits">Из них с верно найденной фиксацией.</param>
/// <param name="LinkerRecall">Recall линкера; <c>null</c>, если метрика не применима.</param>
/// <param name="ModerationCount">Сколько случаев ушло в модерацию.</param>
/// <param name="ModerationByReason">Распределение модерации по причинам; все пять причин присутствуют, в том числе с нулём.</param>
/// <param name="DropCount">Сколько случаев отброшено.</param>
/// <param name="DropByReason">Распределение отбросов по причинам.</param>
/// <param name="StatusMatrix">Матрица ошибок по статусам.</param>
public sealed record EvalMetrics(
    int CaseCount,
    int RelevantCount,
    int NotRelevantCount,
    int PublishedCount,
    int PublishedCorrect,
    double? Precision,
    int ExtractorTotal,
    int ExtractorHits,
    double? ExtractorRecall,
    bool LinkerApplicable,
    int LinkerTotal,
    int LinkerHits,
    double? LinkerRecall,
    int ModerationCount,
    IReadOnlyDictionary<string, int> ModerationByReason,
    int DropCount,
    IReadOnlyDictionary<string, int> DropByReason,
    EvalStatusMatrix StatusMatrix)
{
    /// <summary>Целевой precision для опубликованных без модерации наблюдений.</summary>
    public const double TargetPrecision = 0.9;

    /// <summary>Целевой recall (экстрактора и линкера).</summary>
    public const double TargetRecall = 0.7;

    /// <summary>Порядок причин модерации в отчёте; совпадает с формулировкой.</summary>
    public static readonly IReadOnlyList<string> ModerationReasons =
    [
        "low_confidence",
        "invalid_status",
        "object_not_found",
        "invalid_response",
        "llm_error",
    ];

    /// <summary>Порядок причин отброса в отчёте.</summary>
    public static readonly IReadOnlyList<string> DropReasons =
    [
        "not_relevant",
        "prefilter_rejected",
        "no_linked_objects",
    ];

    /// <summary>Считает метрики по результатам прогона и конфигурации источника.</summary>
    /// <param name="source">Конфигурация источника: задаёт порядок статусов и применимость линкера.</param>
    /// <param name="cases">Результаты по случаям.</param>
    public static EvalMetrics Calculate(
        EvalSourceConfig source,
        IReadOnlyList<EvalCaseResult> cases)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(cases);

        var relevant = cases.Count(item => item.Case.Expected.Relevant);
        var notRelevant = cases.Count - relevant;

        var published = 0;
        var publishedCorrect = 0;
        foreach (var item in cases)
        {
            published += item.PublishedCount;
            publishedCorrect += item.PublishedCorrectCount;
        }

        var extractorCases = cases
            .Where(item => item.Case.Expected.Relevant && item.Case.Expected.Status is not null)
            .ToArray();
        var extractorHits = extractorCases.Count(item => item.StatusCorrect);

        var linkerApplicable = source.LinkByText && source.Objects.Count > 1;
        var linkerCases = linkerApplicable
            ? cases
                .Where(item => item.Case.Expected.Relevant && item.Case.Expected.ObjectCode is not null)
                .ToArray()
            : [];
        var linkerHits = linkerCases.Count(item => item.ObjectCorrect);

        var moderation = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var reason in ModerationReasons)
        {
            moderation[reason] = 0;
        }

        var moderationCount = 0;
        foreach (var item in cases)
        {
            if (item.Decision != EvalDecisionKind.Moderate)
            {
                continue;
            }

            moderationCount++;
            var reason = item.ModerationReason ?? "invalid_response";
            moderation[reason] = moderation.TryGetValue(reason, out var count) ? count + 1 : 1;
        }

        var drops = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var reason in DropReasons)
        {
            drops[reason] = 0;
        }

        var dropCount = 0;
        foreach (var item in cases)
        {
            if (item.Decision != EvalDecisionKind.Drop)
            {
                continue;
            }

            dropCount++;
            var reason = item.DropReason ?? "not_relevant";
            drops[reason] = drops.TryGetValue(reason, out var count) ? count + 1 : 1;
        }

        return new EvalMetrics(
            cases.Count,
            relevant,
            notRelevant,
            published,
            publishedCorrect,
            published == 0 ? null : (double)publishedCorrect / published,
            extractorCases.Length,
            extractorHits,
            extractorCases.Length == 0 ? null : (double)extractorHits / extractorCases.Length,
            linkerApplicable,
            linkerCases.Length,
            linkerHits,
            !linkerApplicable || linkerCases.Length == 0
                ? null
                : (double)linkerHits / linkerCases.Length,
            moderationCount,
            moderation,
            dropCount,
            drops,
            BuildMatrix(source, cases));
    }

    /// <summary>Доля случаев в процентах для отчёта.</summary>
    /// <param name="count">Число случаев.</param>
    /// <param name="total">Общее число случаев; 0 даёт 0.</param>
    public static double Percent(int count, int total) =>
        total == 0 ? 0 : count * 100d / total;

    /// <summary>Форматирует долю для Markdown и консоли (например, <c>0.667</c>).</summary>
    /// <param name="value">Доля; <c>null</c> — «n/a».</param>
    public static string FormatRatio(double? value) =>
        value is null
            ? "n/a"
            : value.Value.ToString("0.000", CultureInfo.InvariantCulture);

    private static EvalStatusMatrix BuildMatrix(
        EvalSourceConfig source,
        IReadOnlyList<EvalCaseResult> cases)
    {
        var rowLabels = new List<string>(source.Statuses.Count + 2);
        foreach (var status in source.Statuses)
        {
            rowLabels.Add(status.Code);
        }

        if (cases.Any(item => !item.Case.Expected.Relevant))
        {
            rowLabels.Add(EvalStatusMatrix.NotRelevantRow);
        }

        if (cases.Any(item => item.Case.Expected.Relevant && item.Case.Expected.Status is null))
        {
            rowLabels.Add(EvalStatusMatrix.NoExpectedStatusRow);
        }

        var columnLabels = new List<string>(source.Statuses.Count + 2);
        foreach (var status in source.Statuses)
        {
            columnLabels.Add(status.Code);
        }

        var extraColumns = cases
            .Select(ActualStatusCode)
            .Where(code => code is not null &&
                           !source.Statuses.Any(status => string.Equals(status.Code, code, StringComparison.Ordinal)))
            .Select(code => code!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();

        columnLabels.AddRange(extraColumns);
        columnLabels.Add(EvalStatusMatrix.NoStatusColumn);

        var rowIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < rowLabels.Count; index++)
        {
            rowIndex[rowLabels[index]] = index;
        }

        var columnIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < columnLabels.Count; index++)
        {
            columnIndex[columnLabels[index]] = index;
        }

        var cells = new List<int[]>(rowLabels.Count);
        for (var row = 0; row < rowLabels.Count; row++)
        {
            cells.Add(new int[columnLabels.Count]);
        }

        foreach (var item in cases)
        {
            var rowLabel = RowLabel(item.Case.Expected);
            var columnLabel = ActualStatusCode(item) ?? EvalStatusMatrix.NoStatusColumn;

            if (!rowIndex.TryGetValue(rowLabel, out var row) ||
                !columnIndex.TryGetValue(columnLabel, out var column))
            {
                continue;
            }

            cells[row][column]++;
        }

        return new EvalStatusMatrix(
            rowLabels,
            columnLabels,
            cells.Select(row => (IReadOnlyList<int>)row).ToArray());
    }

    private static string RowLabel(EvalExpectation expected)
    {
        if (!expected.Relevant)
        {
            return EvalStatusMatrix.NotRelevantRow;
        }

        return expected.Status ?? EvalStatusMatrix.NoExpectedStatusRow;
    }

    private static string? ActualStatusCode(EvalCaseResult item) =>
        item.Decision switch
        {
            EvalDecisionKind.Propose => item.Proposed.Count == 0 ? null : item.Proposed[0].StatusCode,
            EvalDecisionKind.Moderate => item.ProposedStatusCode,
            _ => null,
        };
}
