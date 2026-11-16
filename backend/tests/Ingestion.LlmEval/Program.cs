using System.Globalization;

using Ingestion.Abstractions;
using Ingestion.Llm.OpenAi;
using Ingestion.LlmEval.Core;

using Microsoft.Extensions.Logging.Abstractions;

namespace Ingestion.LlmEval;

/// <summary>
/// Консольный раннер оценки качества LLM. Прогоняет размеченный JSONL-набор
/// через продуктовый <see cref="Ingestion.Pipeline.MessagePipeline"/>, пишет Markdown- и
/// JSON-отчёт и в регрессионном режиме возвращает код 1 при просадке precision.
/// </summary>
/// <remarks>
/// Ключ LLM берётся только из окружения (<c>MAYAK_LLM_API_KEY</c>) и никогда не печатается
/// и не попадает в отчёт. Реальный размеченный набор готовит заказчик; <c>fake</c>-провайдер
/// нужен для проверки инструмента без ключа.
/// </remarks>
public static class EvalProgram
{
    private const string Usage =
        """
        Оценка качества LLM-разбора.

        Использование:
          dotnet run --project tests/Ingestion.LlmEval -- --dataset <файл>.jsonl [параметры]

        Обязательные:
          --dataset <файл>       JSONL-набор: по одному размеченному сообщению в строке.

        Необязательные:
          --prompt <файл>        Заменить промпт-фильтр источника текстом из файла.
          --sources <файл>       JSON-конфигурация источника (статусы, фиксации, режим, порог).
                                 Без параметра используется встроенный пример из README.
          --out <файл.md|json>   Куда писать отчёт. Пишутся оба формата с общим базовым именем;
                                 по умолчанию llm-eval-report.md и llm-eval-report.json в текущем каталоге.
          --provider <openai|fake>
                                 openai — реальная модель из MAYAK_LLM_* (по умолчанию);
                                 fake — детерминированная эвристика для проверки инструмента.
          --baseline <отчёт.json>
                                 Предыдущий JSON-отчёт: включить регрессионную проверку.
          --min-precision <0..1> Абсолютный порог precision в регрессионном режиме (по умолчанию 0.95).
          --help                 Показать эту справку.

        Переменные окружения для --provider openai:
          MAYAK_LLM_API_KEY      API-ключ (обязателен; в отчёт и логи не попадает)
          MAYAK_LLM_BASE_URL     Base URL OpenAI-совместимого API (по умолчанию https://api.openai.com/v1)
          MAYAK_LLM_MODEL        Имя модели (обязательно)
          MAYAK_LLM_PROXY        Прокси для запросов к LLM (необязательно)

        Коды возврата:
          0 — прогон успешен (или регрессия не обнаружена);
          1 — регрессия: precision ниже порога или просадка более 2 п.п.;
          2 — ошибка аргументов, набора или конфигурации;
          3 — внутренняя ошибка;
          130 — прогон отменён.
        """;

    /// <summary>Точка входа консольного раннера.</summary>
    /// <param name="args">Аргументы командной строки.</param>
    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        try
        {
            return await RunAsync(args, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.Error.WriteLine("Прогон отменён.");
            return 130;
        }
        catch (EvalDatasetException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (EvalConfigurationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            // Ключ LLM сюда попасть не может: он не входит ни в одно сообщение исключений провайдера.
            Console.Error.WriteLine($"Внутренняя ошибка: {ex.Message}");
            return 3;
        }
    }

    private static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var options = CliOptions.Parse(args);

        if (options.Help || options.Dataset is null)
        {
            Console.Out.WriteLine(Usage);
            return options.Help ? 0 : 2;
        }

        var source = options.Sources is null
            ? EvalSourceConfig.Default
            : EvalSourceConfig.LoadFile(options.Sources);

        if (options.Prompt is not null)
        {
            source = await ApplyPromptFileAsync(source, options.Prompt, ct).ConfigureAwait(false);
        }

        var cases = EvalDataset.LoadFile(options.Dataset);

        using var provider = CreateProvider(options.Provider, source);

        var runner = new EvalRunner(provider.Llm);
        var run = await runner
            .RunAsync(source, cases, ct)
            .ConfigureAwait(false);

        var info = new EvalRunInfo(
            provider.Name,
            provider.Model,
            EvalRunner.ComputePromptHash(source),
            options.Dataset,
            cases.Count,
            source.Name,
            source.SourceId,
            source.LinkByText,
            source.ConfidenceThreshold,
            source.Statuses.Select(status => status.Code).ToArray(),
            source.Objects.Select(mapObject => mapObject.Code).ToArray());

        var report = EvalReport.Create(run, info, DateTimeOffset.UtcNow);

        BaselineComparison? comparison = null;
        if (options.Baseline is not null)
        {
            var baselineJson = await ReadTextAsync(
                options.Baseline,
                "отчёт baseline",
                ct).ConfigureAwait(false);

            var baseline = EvalBaseline.ReadSnapshot(baselineJson);
            comparison = EvalBaseline.Compare(report, baseline, options.MinPrecision);
        }

        var (markdownPath, jsonPath) = ResolveReportPaths(options.Out);
        await EvalReportWriter
            .WriteAsync(report, markdownPath, jsonPath, comparison, ct)
            .ConfigureAwait(false);

        PrintSummary(report, markdownPath, jsonPath, comparison);

        return comparison?.ExitCode ?? 0;
    }

    private static async Task<EvalSourceConfig> ApplyPromptFileAsync(
        EvalSourceConfig source,
        string path,
        CancellationToken ct)
    {
        var prompt = await ReadTextAsync(path, "файл промпта", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new EvalConfigurationException($"Файл промпта \"{path}\" пуст.");
        }

        return source with { UserPrompt = prompt.Trim() };
    }

    private static async Task<string> ReadTextAsync(
        string path,
        string what,
        CancellationToken ct)
    {
        try
        {
            return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new EvalConfigurationException($"Не удалось прочитать {what} \"{path}\": {ex.Message}", ex);
        }
    }

    private static ProviderHandle CreateProvider(string providerName, EvalSourceConfig source) =>
        providerName switch
        {
            "fake" => new ProviderHandle(
                new HeuristicLlmProvider(source),
                "fake",
                "fake-heuristic",
                disposable: null),

            "openai" => CreateOpenAiProvider(),

            _ => throw new EvalConfigurationException(
                $"Неизвестный провайдер \"{providerName}\": допустимы openai и fake."),
        };

    private static ProviderHandle CreateOpenAiProvider()
    {
        var apiKey = Environment.GetEnvironmentVariable(EnvLlmSettingsSource.ApiKeyVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new EvalConfigurationException(
                $"Не задана переменная окружения {EnvLlmSettingsSource.ApiKeyVariable}. " +
                "Для проверки инструмента без ключа используйте --provider fake.");
        }

        var model = Environment.GetEnvironmentVariable(EnvLlmSettingsSource.ModelVariable);
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new EvalConfigurationException(
                $"Не задана переменная окружения {EnvLlmSettingsSource.ModelVariable} " +
                "(имя модели обязательно для --provider openai).");
        }

        var monitor = new OpenAiOptionsMonitor(new EnvLlmSettingsSource(), TimeProvider.System);
        var provider = new OpenAiProvider(monitor, NullLogger<OpenAiProvider>.Instance);

        return new ProviderHandle(provider, "openai", model.Trim(), new DisposableGroup(provider, monitor));
    }

    private static (string MarkdownPath, string JsonPath) ResolveReportPaths(string? outPath)
    {
        if (string.IsNullOrWhiteSpace(outPath))
        {
            return ("llm-eval-report.md", "llm-eval-report.json");
        }

        var extension = Path.GetExtension(outPath);

        if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            return (Path.ChangeExtension(outPath, ".md"), outPath);
        }

        if (string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase))
        {
            return (outPath, Path.ChangeExtension(outPath, ".json"));
        }

        return (outPath + ".md", outPath + ".json");
    }

    private static void PrintSummary(
        EvalReport report,
        string markdownPath,
        string jsonPath,
        BaselineComparison? comparison)
    {
        var metrics = report.Metrics;

        Console.Out.WriteLine(
            $"Набор: {report.Run.DatasetPath} ({CasesText(report.Run.DatasetSize)})");
        Console.Out.WriteLine(
            $"Провайдер: {report.Run.Provider} ({report.Run.Model}); хеш промпта: {report.Run.PromptHash}");
        Console.Out.WriteLine(
            $"Precision опубликованных наблюдений: {Ratio(metrics.Precision)} " +
            $"({metrics.PublishedCorrect} из {metrics.PublishedCount}; цель ≥ {EvalMetrics.TargetPrecision.ToString("0.00", CultureInfo.InvariantCulture)})");
        Console.Out.WriteLine(
            $"Recall экстрактора: {Ratio(metrics.ExtractorRecall)} " +
            $"({metrics.ExtractorHits} из {metrics.ExtractorTotal}; цель ≥ {EvalMetrics.TargetRecall.ToString("0.00", CultureInfo.InvariantCulture)})");
        Console.Out.WriteLine(
            $"Recall линкера: {Ratio(metrics.LinkerRecall)} " +
            (metrics.LinkerApplicable
                ? $"({metrics.LinkerHits} из {metrics.LinkerTotal})"
                : "(не применим: режим «по тексту» выключен или одна фиксация)"));
        var moderationReasons = metrics.ModerationByReason
            .Where(pair => pair.Value > 0)
            .Select(pair => $"{pair.Key} {pair.Value}")
            .ToArray();

        Console.Out.WriteLine(
            $"Модерация: {metrics.ModerationCount} из {metrics.CaseCount} " +
            $"({EvalMetrics.Percent(metrics.ModerationCount, metrics.CaseCount).ToString("0.0", CultureInfo.InvariantCulture)}%): " +
            (moderationReasons.Length == 0 ? "нет" : string.Join(", ", moderationReasons)));
        Console.Out.WriteLine($"Отброшено: {metrics.DropCount} из {metrics.CaseCount}");

        if (comparison is not null)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine(
                $"Регрессия: {(comparison.Failed ? "ПРОВАЛ" : "OK")} — {comparison.Summary}");
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine($"Отчёт: {markdownPath}");
        Console.Out.WriteLine($"Отчёт: {jsonPath}");
    }

    private static string Ratio(double? value) =>
        value is null
            ? "n/a"
            : value.Value.ToString("0.000", CultureInfo.InvariantCulture);

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

    private sealed class ProviderHandle(
        ILlmProvider llm,
        string name,
        string model,
        IDisposable? disposable) : IDisposable
    {
        public ILlmProvider Llm { get; } = llm;

        public string Name { get; } = name;

        public string Model { get; } = model;

        public void Dispose() => disposable?.Dispose();
    }

    private sealed class DisposableGroup(params IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items)
            {
                item.Dispose();
            }
        }
    }

    private sealed class CliOptions
    {
        public string? Dataset { get; private set; }

        public string? Prompt { get; private set; }

        public string? Sources { get; private set; }

        public string? Out { get; private set; }

        public string Provider { get; private set; } = "openai";

        public string? Baseline { get; private set; }

        public double MinPrecision { get; private set; } = EvalBaseline.DefaultMinPrecision;

        public bool Help { get; private set; }

        public static CliOptions Parse(string[] args)
        {
            var options = new CliOptions();
            var index = 0;

            while (index < args.Length)
            {
                var argument = args[index];
                index++;

                if (argument is "--help" or "-h")
                {
                    options.Help = true;
                    continue;
                }

                if (!argument.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new EvalConfigurationException(
                        $"Неизвестный аргумент \"{argument}\". Используйте --help.");
                }

                var name = argument[2..];
                var value = TakeValue(args, ref index, name);

                switch (name)
                {
                    case "dataset":
                        options.Dataset = value;
                        break;
                    case "prompt":
                        options.Prompt = value;
                        break;
                    case "sources":
                        options.Sources = value;
                        break;
                    case "out":
                        options.Out = value;
                        break;
                    case "provider":
                        options.Provider = value.ToLowerInvariant();
                        if (options.Provider is not ("openai" or "fake"))
                        {
                            throw new EvalConfigurationException(
                                $"Неизвестный провайдер \"{value}\": допустимы openai и fake.");
                        }

                        break;
                    case "baseline":
                        options.Baseline = value;
                        break;
                    case "min-precision":
                        if (!double.TryParse(
                                value,
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out var minPrecision) ||
                            !double.IsFinite(minPrecision) ||
                            minPrecision is < 0d or > 1d)
                        {
                            throw new EvalConfigurationException(
                                $"--min-precision должен быть числом от 0 до 1, получено \"{value}\".");
                        }

                        options.MinPrecision = minPrecision;
                        break;
                    default:
                        throw new EvalConfigurationException(
                            $"Неизвестный параметр --{name}. Используйте --help.");
                }
            }

            return options;
        }

        private static string TakeValue(string[] args, ref int index, string name)
        {
            if (index >= args.Length)
            {
                throw new EvalConfigurationException($"У параметра --{name} нет значения.");
            }

            var value = args[index];
            index++;

            return value;
        }
    }
}
