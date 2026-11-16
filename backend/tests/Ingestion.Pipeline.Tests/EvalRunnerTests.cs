using System.Globalization;
using System.Text.Json;

using Ingestion.Abstractions;
using Ingestion.LlmEval.Core;

using Shouldly;

namespace Ingestion.Pipeline.Tests;

/// <summary>
/// Тесты инструмента оценки LLM: метрики на известном наборе, регрессионный
/// порог, отсутствие ключа в отчёте и понятная ошибка JSONL с номером строки.
/// </summary>
public sealed class EvalRunnerTests
{
    private static readonly Guid SourceId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    private const string NoWaterText = "В центре нет воды";
    private const string WaterOkText = "На севере вода есть";

    [Fact]
    public async Task Metrics_are_calculated_on_known_dataset()
    {
        var source = TestSource();
        var cases = new[]
        {
            Case("c1", NoWaterText, status: "no_water", objectCode: "center"),
            Case("c2", WaterOkText, status: "water_ok", objectCode: "north"),
            Case("c3", "В центре нет воды, ждём", status: "no_water", objectCode: "center"),
            Case("c4", WaterOkText, status: "water_ok", objectCode: "north"),
            Case("c5", "Продам гараж", relevant: false),
            Case("c6", NoWaterText, status: "no_water", objectCode: "center"),
        };

        var provider = new ScriptedLlmProvider(
        [
            Response(true, "no_water", source.ObjectId("center"), 0.9, "нет воды"),
            Response(true, "water_ok", source.ObjectId("north"), 0.9, "вода есть"),
            Response(true, "no_water", source.ObjectId("center"), 0.5, "нет воды"),
            Response(true, "no_water", source.ObjectId("north"), 0.9, "вода есть"),
            Response(false),
            "это не JSON",
        ]);

        var metrics = (await new EvalRunner(provider).RunAsync(source, cases, CancellationToken.None)).Metrics;

        // Precision: опубликованы наблюдения в случаях c1, c2 и c4; верны c1 и c2.
        metrics.PublishedCount.ShouldBe(3);
        metrics.PublishedCorrect.ShouldBe(2);
        metrics.Precision.ShouldNotBeNull();
        metrics.Precision!.Value.ShouldBe(2.0 / 3.0, 1e-9);

        // Recall экстрактора: c1–c4 и c6; верный статус предложен в c1, c2 и c3 (модерация).
        metrics.ExtractorTotal.ShouldBe(5);
        metrics.ExtractorHits.ShouldBe(3);
        metrics.ExtractorRecall!.Value.ShouldBe(0.6, 1e-9);

        // Recall линкера: та же пятёрка; фиксация верна в c1–c4, в c6 ответа нет.
        metrics.LinkerApplicable.ShouldBeTrue();
        metrics.LinkerTotal.ShouldBe(5);
        metrics.LinkerHits.ShouldBe(4);
        metrics.LinkerRecall!.Value.ShouldBe(0.8, 1e-9);

        metrics.ModerationCount.ShouldBe(2);
        metrics.ModerationByReason["low_confidence"].ShouldBe(1);
        metrics.ModerationByReason["invalid_response"].ShouldBe(1);
        metrics.ModerationByReason["invalid_status"].ShouldBe(0);
        metrics.DropCount.ShouldBe(1);
        metrics.DropByReason["not_relevant"].ShouldBe(1);

        // Матрица: ожидаемый no_water встречается в c1, c3 и c6; в c4 модель предложила no_water
        // при ожидании water_ok — это отдельная ячейка water_ok × no_water.
        MatrixCell(metrics.StatusMatrix, "no_water", "no_water").ShouldBe(2);
        MatrixCell(metrics.StatusMatrix, "no_water", EvalStatusMatrix.NoStatusColumn).ShouldBe(1);
        MatrixCell(metrics.StatusMatrix, "water_ok", "water_ok").ShouldBe(1);
        MatrixCell(metrics.StatusMatrix, "water_ok", "no_water").ShouldBe(1);
        MatrixCell(metrics.StatusMatrix, EvalStatusMatrix.NotRelevantRow, EvalStatusMatrix.NoStatusColumn)
            .ShouldBe(1);
    }

    [Fact]
    public async Task Regression_fails_when_precision_drops()
    {
        var source = SingleObjectSource();
        var cases = new[]
        {
            Case("c1", NoWaterText, status: "no_water", objectCode: "center"),
            Case("c2", NoWaterText, status: "no_water", objectCode: "center"),
        };

        var baselineRun = await new EvalRunner(new ScriptedLlmProvider(
        [
            Response(true, "no_water", source.ObjectId("center"), 0.9, "нет воды"),
            Response(true, "no_water", source.ObjectId("center"), 0.9, "нет воды"),
        ])).RunAsync(source, cases, CancellationToken.None);

        var currentRun = await new EvalRunner(new ScriptedLlmProvider(
        [
            Response(true, "no_water", source.ObjectId("center"), 0.9, "нет воды"),
            Response(true, "water_ok", source.ObjectId("center"), 0.9, "нет воды"),
        ])).RunAsync(source, cases, CancellationToken.None);

        var baseline = EvalReport.Create(baselineRun, Info(), DateTimeOffset.UnixEpoch);
        baseline.Metrics.Precision!.Value.ShouldBe(1.0, 1e-9);

        var current = EvalReport.Create(currentRun, Info(), DateTimeOffset.UnixEpoch);
        current.Metrics.Precision!.Value.ShouldBe(0.5, 1e-9);

        var comparison = EvalBaseline.Compare(
            current,
            EvalBaseline.ReadSnapshot(baseline.ToJson()),
            minPrecision: 0.5);

        comparison.BelowMinPrecision.ShouldBeFalse();
        comparison.Regressed.ShouldBeTrue();
        comparison.Failed.ShouldBeTrue();
        comparison.ExitCode.ShouldBe(1);
        comparison.DropPercentagePoints.ShouldBe(50.0, 1e-9);
    }

    [Fact]
    public async Task Regression_within_two_points_does_not_fail()
    {
        var source = SingleObjectSource();
        var cases = Enumerable.Range(1, 100)
            .Select(index => Case($"c{index}", NoWaterText, status: "no_water", objectCode: "center"))
            .ToArray();

        var correct = Response(true, "no_water", source.ObjectId("center"), 0.9, "нет воды");
        var wrong = Response(true, "water_ok", source.ObjectId("center"), 0.9, "нет воды");

        var baselineRun = await new EvalRunner(new ScriptedLlmProvider(
            Enumerable.Repeat(correct, 100))).RunAsync(source, cases, CancellationToken.None);

        var currentRun = await new EvalRunner(new ScriptedLlmProvider(
            Enumerable.Repeat(correct, 99).Append(wrong))).RunAsync(source, cases, CancellationToken.None);

        var baseline = EvalReport.Create(baselineRun, Info(), DateTimeOffset.UnixEpoch);
        var current = EvalReport.Create(currentRun, Info(), DateTimeOffset.UnixEpoch);

        baseline.Metrics.Precision!.Value.ShouldBe(1.0, 1e-9);
        current.Metrics.Precision!.Value.ShouldBe(0.99, 1e-9);

        var comparison = EvalBaseline.Compare(
            current,
            EvalBaseline.ReadSnapshot(baseline.ToJson()),
            minPrecision: 0.5);

        comparison.DropPercentagePoints.ShouldBe(1.0, 1e-9);
        comparison.Regressed.ShouldBeFalse();
        comparison.Failed.ShouldBeFalse();
        comparison.ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task Llm_key_never_appears_in_report()
    {
        const string apiKey = "sk-test-mayak-secret-do-not-leak-42";
        var previousKey = Environment.GetEnvironmentVariable(EnvLlmSettingsSource.ApiKeyVariable);
        var previousModel = Environment.GetEnvironmentVariable(EnvLlmSettingsSource.ModelVariable);

        try
        {
            Environment.SetEnvironmentVariable(EnvLlmSettingsSource.ApiKeyVariable, apiKey);
            Environment.SetEnvironmentVariable(EnvLlmSettingsSource.ModelVariable, "gpt-test-model");

            var settings = await new EnvLlmSettingsSource().ReadAsync(CancellationToken.None);
            settings.ShouldNotBeNull();
            settings!.Model.ShouldBe("gpt-test-model");

            var source = TestSource();
            var cases = new[]
            {
                Case("c1", NoWaterText, status: "no_water", objectCode: "center"),
                Case("c2", "Продам гараж", relevant: false),
            };

            var run = await new EvalRunner(new ScriptedLlmProvider(
            [
                Response(true, "no_water", source.ObjectId("center"), 0.9, "нет воды"),
                Response(false),
            ])).RunAsync(source, cases, CancellationToken.None);

            var info = Info() with
            {
                Provider = "openai",
                Model = settings.Model,
                DatasetSize = cases.Length,
            };

            var report = EvalReport.Create(run, info, DateTimeOffset.UnixEpoch);

            var json = report.ToJson();
            var markdown = report.ToMarkdown();

            json.ShouldNotContain(apiKey);
            markdown.ShouldNotContain(apiKey);
            json.ShouldContain("gpt-test-model");
            json.ShouldContain("sha256:");
            markdown.ShouldContain("gpt-test-model");

            var directory = Path.Combine(
                Path.GetTempPath(),
                "mayak-eval-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            var markdownPath = Path.Combine(directory, "report.md");
            var jsonPath = Path.Combine(directory, "report.json");

            try
            {
                await EvalReportWriter.WriteAsync(
                    report,
                    markdownPath,
                    jsonPath,
                    comparison: null,
                    CancellationToken.None);

                (await File.ReadAllTextAsync(markdownPath)).ShouldNotContain(apiKey);
                (await File.ReadAllTextAsync(jsonPath)).ShouldNotContain(apiKey);
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvLlmSettingsSource.ApiKeyVariable, previousKey);
            Environment.SetEnvironmentVariable(EnvLlmSettingsSource.ModelVariable, previousModel);
        }
    }

    [Fact]
    public async Task Llm_error_is_reported_as_separate_moderation_reason()
    {
        var source = SingleObjectSource();
        var cases = new[]
        {
            Case("c1", NoWaterText, status: "no_water", objectCode: "center"),
        };

        var run = await new EvalRunner(new ThrowingLlmProvider()).RunAsync(
            source,
            cases,
            CancellationToken.None);

        run.Cases.ShouldHaveSingleItem().ModerationReason.ShouldBe("llm_error");
        run.Metrics.ModerationByReason["llm_error"].ShouldBe(1);
        run.Metrics.ModerationByReason["invalid_response"].ShouldBe(0);
        run.Metrics.Precision.ShouldBeNull();
    }

    [Fact]
    public void Invalid_jsonl_line_reports_line_number()
    {
        string[] lines =
        [
            """{"text":"В центре нет воды","expected":{"relevant":true,"status":"no_water","objectCode":"center"}}""",
            """{"text":"сломано","expected":""",
            """{"text":"На севере вода есть","expected":{"relevant":true,"status":"water_ok","objectCode":"north"}}""",
        ];

        var error = Should.Throw<EvalDatasetException>(
            () => EvalDataset.Parse(lines, "cases.jsonl"));

        error.LineNumber.ShouldBe(2);
        error.Message.ShouldContain("строка 2");
        error.Message.ShouldContain("cases.jsonl");
        error.Message.ShouldContain("JSON");
    }

    [Fact]
    public void Dataset_with_missing_expected_field_reports_line_number()
    {
        string[] lines =
        [
            """{"text":"В центре нет воды","expected":{"relevant":true,"status":"no_water","objectCode":"center"}}""",
            """{"text":"На севере вода есть"}""",
        ];

        var error = Should.Throw<EvalDatasetException>(
            () => EvalDataset.Parse(lines, "cases.jsonl"));

        error.LineNumber.ShouldBe(2);
        error.Message.ShouldContain("expected");
    }

    [Fact]
    public void Expected_codes_must_exist_in_source_configuration()
    {
        var source = TestSource();
        var cases = new[]
        {
            Case("c1", NoWaterText, status: "flood", objectCode: "center"),
        };

        var error = Should.Throw<EvalConfigurationException>(
            () => EvalDataset.ValidateAgainst(cases, source));

        error.Message.ShouldContain("flood");
    }

    [Fact]
    public void Sample_dataset_and_sources_are_consistent()
    {
        var root = FindRepositoryRoot();
        if (root is null)
        {
            return;
        }

        var datasetPath = Path.Combine(root, "datasets", "llm-eval", "sample.jsonl");
        var sourcesPath = Path.Combine(root, "datasets", "llm-eval", "sample.sources.json");

        File.Exists(datasetPath).ShouldBeTrue();
        File.Exists(sourcesPath).ShouldBeTrue();

        var source = EvalSourceConfig.LoadFile(sourcesPath);
        var cases = EvalDataset.LoadFile(datasetPath);

        cases.Count.ShouldBeGreaterThanOrEqualTo(12);
        EvalDataset.ValidateAgainst(cases, source);

        source.Statuses.Select(status => status.Code)
            .ShouldBe(EvalSourceConfig.Default.Statuses.Select(status => status.Code));
        source.Objects.Select(mapObject => mapObject.Code)
            .ShouldBe(EvalSourceConfig.Default.Objects.Select(mapObject => mapObject.Code));

        cases.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count()
            .ShouldBe(cases.Count);
    }

    private static EvalSourceConfig TestSource() => new(
        SourceId,
        "Тестовый источник",
        "ищи сообщения о воде",
        LinkByText: true,
        ConfidenceThreshold: 0.7,
        [
            new EvalStatusConfig("water_ok", "Вода есть", "Подача идёт.", []),
            new EvalStatusConfig("no_water", "Нет воды", "Подачи нет.", []),
        ],
        [
            new EvalObjectConfig("center", "Центр", null, []),
            new EvalObjectConfig("north", "Север", null, []),
        ],
        Prefilter: null);

    private static EvalSourceConfig SingleObjectSource() => new(
        SourceId,
        "Тестовый источник",
        "ищи сообщения о воде",
        LinkByText: false,
        ConfidenceThreshold: 0.7,
        [
            new EvalStatusConfig("water_ok", "Вода есть", "Подача идёт.", []),
            new EvalStatusConfig("no_water", "Нет воды", "Подачи нет.", []),
        ],
        [
            new EvalObjectConfig("center", "Центр", null, []),
        ],
        Prefilter: null);

    private static EvalCase Case(
        string id,
        string text,
        string? status = null,
        string? objectCode = null,
        bool relevant = true) =>
        new(id, text, new EvalExpectation(relevant, status, objectCode), Note: null);

    private static EvalRunInfo Info() => new(
        "fake",
        "fake-heuristic",
        "sha256:test",
        "cases.jsonl",
        2,
        "Тестовый источник",
        SourceId,
        LinkByText: true,
        ConfidenceThreshold: 0.7,
        ["water_ok", "no_water"],
        ["center", "north"]);

    private static string Response(
        bool relevant,
        string? status = null,
        Guid? objectId = null,
        double confidence = 0d,
        string quote = "") =>
        JsonSerializer.Serialize(new
        {
            relevant,
            status,
            object_id = objectId?.ToString("D"),
            confidence,
            evidence_quote = quote,
        });

    private static int MatrixCell(EvalStatusMatrix matrix, string expectedRow, string actualColumn)
    {
        var row = matrix.ExpectedRows
            .Select((label, index) => (label, index))
            .Single(pair => string.Equals(pair.label, expectedRow, StringComparison.Ordinal))
            .index;

        var column = matrix.ActualColumns
            .Select((label, index) => (label, index))
            .Single(pair => string.Equals(pair.label, actualColumn, StringComparison.Ordinal))
            .index;

        return matrix.Cells[row][column];
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "backend", "Mayak.slnx")) &&
                Directory.Exists(Path.Combine(directory.FullName, "datasets")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>Провайдер, имитирующий недоступность LLM: pipeline обязан увести случай в модерацию.</summary>
    private sealed class ThrowingLlmProvider : ILlmProvider
    {
        public Task<string> CompleteAsync(LlmRequest request, CancellationToken ct) =>
            throw new HttpRequestException("LLM недоступен");
    }

    /// <summary>Провайдер с заранее заданной очередью ответов; запоминает запросы.</summary>
    private sealed class ScriptedLlmProvider(IEnumerable<string> responses) : ILlmProvider
    {
        private readonly Queue<string> _responses = new(responses);

        public List<LlmRequest> Requests { get; } = [];

        public Task<string> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("Скрипт ответов LLM исчерпан.");
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }
}
