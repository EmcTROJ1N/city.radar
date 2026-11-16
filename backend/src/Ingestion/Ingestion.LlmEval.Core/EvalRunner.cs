using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Ingestion.Abstractions;
using Ingestion.Pipeline;

namespace Ingestion.LlmEval.Core;

/// <summary>
/// Прогон размеченного набора через тот же <see cref="MessagePipeline"/>, что и Worker
///): провайдер LLM подменяется (реальный OpenAI или fake), а промпты, валидация статуса,
/// проверка цитаты, порог confidence и решение «карта или модерация» остаются продуктовыми.
/// </summary>
/// <remarks>
/// Pipeline не знает про набор: раннер подставляет текст и снимок конфигурации источника,
/// поэтому метрики отражают именно то поведение, которое увидит пользователь в Worker-е.
/// </remarks>
public sealed class EvalRunner
{
    /// <summary>
    /// Фиксированное время публикации сообщения набора. Нужно только для <c>ObservedAt</c>
    /// в предложениях; на решение и метрики не влияет и делает прогоны воспроизводимыми.
    /// </summary>
    public static readonly DateTimeOffset PostedAt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly ILlmProvider _llm;

    /// <summary>Создаёт раннер поверх провайдера LLM.</summary>
    /// <param name="llm">Реальный или fake-провайдер; ключ и настройки сюда не передаются.</param>
    public EvalRunner(ILlmProvider llm)
    {
        ArgumentNullException.ThrowIfNull(llm);
        _llm = llm;
    }

    /// <summary>Прогоняет все случаи набора через pipeline и считает метрики.</summary>
    /// <param name="source">Конфигурация источника.</param>
    /// <param name="cases">Размеченные случаи.</param>
    /// <param name="ct">Токен отмены.</param>
    public async Task<EvalRunResult> RunAsync(
        EvalSourceConfig source,
        IReadOnlyList<EvalCase> cases,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(cases);

        // Опечатка в разметке не должна выглядеть как ошибка модели.
        EvalDataset.ValidateAgainst(cases, source);

        var snapshot = source.ToSnapshot();
        var pipeline = new MessagePipeline(_llm);
        var results = new List<EvalCaseResult>(cases.Count);

        for (var index = 0; index < cases.Count; index++)
        {
            ct.ThrowIfCancellationRequested();

            var item = cases[index];
            var message = new RawMessage(
                source.SourceId,
                string.Create(CultureInfo.InvariantCulture, $"eval-{index + 1}"),
                item.Text,
                Author: null,
                Chat: null,
                Link: null,
                PostedAt);

            var rawMessageId = EvalIdentity.For(
                source.SourceId,
                "raw",
                string.Create(CultureInfo.InvariantCulture, $"{(index + 1).ToString(CultureInfo.InvariantCulture)}"));

            var decision = await pipeline
                .ProcessAsync(message, rawMessageId, snapshot, ct)
                .ConfigureAwait(false);

            results.Add(MapDecision(source, item, decision));
        }

        return new EvalRunResult(source, results, EvalMetrics.Calculate(source, results));
    }

    /// <summary>
    /// Хеш промпта источника: SHA-256 от системного и пользовательского промпта, собранных
    /// <see cref="SystemPromptBuilder"/>. Позволяет сравнить прогоны «до и после» правки промпта,
    /// не сохраняя сам текст промпта в отчёте.
    /// </summary>
    /// <param name="source">Конфигурация источника.</param>
    public static string ComputePromptHash(EvalSourceConfig source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var snapshot = source.ToSnapshot();
        var objectsForPrompt = snapshot.LinkByText && snapshot.LinkedObjects.Count > 1
            ? snapshot.LinkedObjects
            : null;

        var (system, user) = SystemPromptBuilder.Build(
            new SourcePromptContext(snapshot.Statuses, snapshot.UserPrompt, objectsForPrompt));

        var material = system + "\n\u0000\n" + user;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));

        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static EvalCaseResult MapDecision(
        EvalSourceConfig source,
        EvalCase item,
        PipelineDecision decision)
    {
        var expected = item.Expected;

        switch (decision)
        {
            case PipelineDecision.Propose propose:
            {
                var proposed = propose.Observations
                    .Select(observation => new EvalProposedObservation(
                        observation.StatusCode,
                        source.ObjectCode(observation.MapObjectId)))
                    .ToArray();

                var statusCorrect = expected.Relevant &&
                                    expected.Status is not null &&
                                    proposed.Any(observation =>
                                        string.Equals(observation.StatusCode, expected.Status, StringComparison.Ordinal));

                var objectCorrect = expected.Relevant &&
                                    expected.ObjectCode is not null &&
                                    proposed.Any(observation =>
                                        string.Equals(observation.ObjectCode, expected.ObjectCode, StringComparison.Ordinal));

                var publishedCorrect = proposed.Count(observation => IsPublishedCorrect(expected, observation));

                return new EvalCaseResult(
                    item,
                    EvalDecisionKind.Propose,
                    proposed,
                    ProposedStatusCode: null,
                    ProposedObjectCode: null,
                    ModerationReason: null,
                    DropReason: null,
                    statusCorrect,
                    objectCorrect,
                    IsCorrect(expected, EvalDecisionKind.Propose, statusCorrect, objectCorrect),
                    proposed.Length,
                    publishedCorrect);
            }

            case PipelineDecision.Moderate moderate:
            {
                var request = moderate.Request;
                var proposedObjectCode = request.ProposedMapObjectId is { } mapObjectId
                    ? source.ObjectCode(mapObjectId)
                    : null;

                var statusCorrect = expected.Relevant &&
                                    expected.Status is not null &&
                                    string.Equals(
                                        request.ProposedStatusCode,
                                        expected.Status,
                                        StringComparison.Ordinal);

                var objectCorrect = expected.Relevant &&
                                    expected.ObjectCode is not null &&
                                    string.Equals(proposedObjectCode, expected.ObjectCode, StringComparison.Ordinal);

                return new EvalCaseResult(
                    item,
                    EvalDecisionKind.Moderate,
                    Proposed: [],
                    request.ProposedStatusCode,
                    proposedObjectCode,
                    ModerationReason(request.Kind),
                    DropReason: null,
                    statusCorrect,
                    objectCorrect,
                    IsCorrect(expected, EvalDecisionKind.Moderate, statusCorrect, objectCorrect),
                    PublishedCount: 0,
                    PublishedCorrectCount: 0);
            }

            case PipelineDecision.Drop drop:
            {
                return new EvalCaseResult(
                    item,
                    EvalDecisionKind.Drop,
                    Proposed: [],
                    ProposedStatusCode: null,
                    ProposedObjectCode: null,
                    ModerationReason: null,
                    MapDropReason(drop.Reason),
                    StatusCorrect: false,
                    ObjectCorrect: false,
                    IsCorrect(expected, EvalDecisionKind.Drop, statusCorrect: false, objectCorrect: false),
                    PublishedCount: 0,
                    PublishedCorrectCount: 0);
            }

            default:
                throw new InvalidOperationException(
                    $"Неизвестное решение pipeline: {decision.GetType().Name}.");
        }
    }

    /// <summary>
    /// Опубликованное наблюдение верно, если статус совпал с разметкой и фиксация совпала
    /// (если разметчик фиксацию указал). Для нерелевантного случая верных публикаций нет.
    /// </summary>
    private static bool IsPublishedCorrect(EvalExpectation expected, EvalProposedObservation observation)
    {
        if (!expected.Relevant || expected.Status is null)
        {
            return false;
        }

        if (!string.Equals(observation.StatusCode, expected.Status, StringComparison.Ordinal))
        {
            return false;
        }

        return expected.ObjectCode is null ||
               string.Equals(observation.ObjectCode, expected.ObjectCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Решение по случаю целиком: нерелевантное сообщение должно быть отброшено; релевантное
    /// должно дойти до карты или модерации с верным статусом, а если фиксация размечена —
    /// и с верной фиксацией.
    /// </summary>
    private static bool IsCorrect(
        EvalExpectation expected,
        EvalDecisionKind decision,
        bool statusCorrect,
        bool objectCorrect)
    {
        if (!expected.Relevant)
        {
            return decision == EvalDecisionKind.Drop;
        }

        if (decision == EvalDecisionKind.Drop || !statusCorrect)
        {
            return false;
        }

        return expected.ObjectCode is null || objectCorrect;
    }

    /// <summary>
    /// Причина модерации в терминах. В БД <c>llm_error</c> и «цитата не подстрока»
    /// хранятся как <c>invalid_response</c> (check-констрейнт схемы), но для диагностики качества
    /// отчёт разделяет их: <c>llm_error</c> — сбой провайдера, <c>invalid_response</c> — плохой ответ.
    /// </summary>
    private static string ModerationReason(ModerationKind kind) => kind switch
    {
        ModerationKind.LowConfidence => "low_confidence",
        ModerationKind.StatusNotInList => "invalid_status",
        ModerationKind.ObjectNotFound => "object_not_found",
        ModerationKind.InvalidResponse => "invalid_response",
        ModerationKind.EvidenceNotSubstring => "invalid_response",
        ModerationKind.LlmError => "llm_error",
        _ => "invalid_response",
    };

    private static string MapDropReason(DropReason reason) => reason switch
    {
        DropReason.NotRelevant => "not_relevant",
        DropReason.PrefilterRejected => "prefilter_rejected",
        DropReason.NoLinkedObjects => "no_linked_objects",
        _ => "not_relevant",
    };
}
