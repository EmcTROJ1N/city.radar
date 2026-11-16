using Catalog.Contracts;

using Ingestion.Abstractions;

using Sources.Contracts;

namespace Ingestion.Pipeline;

/// <summary>
/// Снимок конфигурации источника на момент обработки сообщения
///): всё, что pipeline берёт из настроек источника. Изменения в БД
/// применяются к следующему сообщению без перезапуска Worker.
/// </summary>
/// <param name="SourceId">Идентификатор источника.</param>
/// <param name="Statuses">Допустимые статусы источника; ответ LLM обязан совпасть с одним из кодов.</param>
/// <param name="UserPrompt">Промпт-фильтр пользователя из настроек источника.</param>
/// <param name="LinkByText">Включён ли режим «определять фиксацию по тексту».</param>
/// <param name="LinkedObjects">Фиксации, привязанные к источнику; пустой список — применять статус некуда.</param>
/// <param name="ConfidenceThreshold">
/// Порог confidence; уже разрешённое значение — порог источника либо значение по умолчанию
/// из глобальных настроек (это делает вызывающий).
/// </param>
/// <param name="Prefilter">Префильтр источника; <c>null</c> — фильтра нет.</param>
public sealed record SourceRuntimeSnapshot(
    Guid SourceId,
    IReadOnlyList<SourceStatusInfo> Statuses,
    string UserPrompt,
    bool LinkByText,
    IReadOnlyList<LinkedObject> LinkedObjects,
    double ConfidenceThreshold,
    PrefilterConfig? Prefilter);

/// <summary>
/// Pipeline разбора одного сообщения источника (шаги 1–6): префильтр → LLM →
/// проверка ответа кодом → порог confidence → предложения наблюдений или карточка модерации.
/// </summary>
/// <remarks>
/// <para>
/// Текст сообщения — недоверенный ввод, возможен prompt injection. Защита не в «уговорах»
/// модели, а в коде: ответ принимается только в формате схемы, статус — только из списка
/// источника, цитата — только точная подстрока исходного текста (проверяется по полному
/// тексту, а не по усечённому запросу к LLM). Любой сбой разбора или вызова LLM уводит
/// сообщение в модерацию, а не роняет воркер.
/// </para>
/// <para>
/// Промпт собирается для каждого сообщения заново: статусы и промпт-фильтр читаются из
/// настроек источника. Список фиксаций попадает в промпт только в режиме «по тексту»
/// и только при нескольких привязках.
/// </para>
/// </remarks>
public sealed class MessagePipeline
{
    private readonly ILlmProvider _llm;

    /// <summary>Создаёт pipeline поверх провайдера LLM.</summary>
    /// <param name="llm">Провайдер LLM; ошибки провайдера превращаются в карточку модерации.</param>
    public MessagePipeline(ILlmProvider llm)
    {
        ArgumentNullException.ThrowIfNull(llm);
        _llm = llm;
    }

    /// <summary>
    /// Обрабатывает одно сообщение источника и возвращает решение: отброс, предложения
    /// наблюдений либо карточку модерации.
    /// </summary>
    /// <param name="msg">Нормализованное сообщение источника.</param>
    /// <param name="rawMessageId">Идентификатор сохранённой записи <c>raw_messages</c>.</param>
    /// <param name="src">Снимок конфигурации источника.</param>
    /// <param name="ct">Токен отмены; отмена пробрасывается вызывающему.</param>
    /// <returns>Решение pipeline; исключения не ожидаются, кроме отмены.</returns>
    public async Task<PipelineDecision> ProcessAsync(
        RawMessage msg,
        Guid rawMessageId,
        SourceRuntimeSnapshot src,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(msg);
        ArgumentNullException.ThrowIfNull(src);
        ct.ThrowIfCancellationRequested();

        // Шаг 1: дешёвый префильтр до запроса к LLM.
        if (!Prefilter.Passes(msg.Text, src.Prefilter))
        {
            return new PipelineDecision.Drop(DropReason.PrefilterRejected);
        }

        //: без привязанных фиксаций статус применять некуда — в LLM не ходим.
        if (src.LinkedObjects.Count == 0)
        {
            return new PipelineDecision.Drop(DropReason.NoLinkedObjects);
        }

        var multipleObjects = src.LinkedObjects.Count > 1;
        var objectsForPrompt = src.LinkByText && multipleObjects ? src.LinkedObjects : null;

        var (system, user) = SystemPromptBuilder.Build(
            new SourcePromptContext(src.Statuses, src.UserPrompt, objectsForPrompt));

        // В LLM уходит усечённый и обёрнутый текст; цитата проверяется по полному msg.Text.
        var request = new LlmRequest(
            system,
            user,
            PromptSafety.WrapMessage(PromptSafety.TruncateForLlm(msg.Text)));

        // Шаг 2: вызов LLM. Ошибка провайдера — карточка модерации, не падение воркера.
        string? raw;
        try
        {
            raw = await _llm.CompleteAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Moderate(
                src,
                rawMessageId,
                ModerationKind.LlmError,
                $"Ошибка вызова LLM: {ex.Message}");
        }

        var parsed = LlmResponseParser.Parse(raw);
        if (parsed is null)
        {
            return Moderate(
                src,
                rawMessageId,
                ModerationKind.InvalidResponse,
                "Ответ LLM не является корректным JSON ожидаемой схемы.");
        }

        if (!parsed.Relevant)
        {
            return new PipelineDecision.Drop(DropReason.NotRelevant);
        }

        // Шаг 3–4: статус из списка источника, цитата — подстрока текста, фиксация — из привязок.
        var statusCodes = new HashSet<string>(src.Statuses.Select(status => status.Code), StringComparer.Ordinal);
        var sourceObjectIds = src.LinkedObjects.Select(linkedObject => linkedObject.Id).ToHashSet();

        var validation = ExtractionValidator.Validate(
            parsed,
            msg.Text,
            statusCodes,
            sourceObjectIds,
            src.LinkByText,
            multipleObjects);

        switch (validation)
        {
            case ValidationResult.InvalidStatus:
                return Moderate(
                    src,
                    rawMessageId,
                    ModerationKind.StatusNotInList,
                    $"Код статуса \"{parsed.Status}\" отсутствует в списке статусов источника.",
                    parsed);

            case ValidationResult.EvidenceNotSubstring:
                return Moderate(
                    src,
                    rawMessageId,
                    ModerationKind.EvidenceNotSubstring,
                    "Цитата evidence_quote не является точной подстрокой текста сообщения.",
                    parsed);

            case ValidationResult.ObjectNotFound:
                return Moderate(
                    src,
                    rawMessageId,
                    ModerationKind.ObjectNotFound,
                    "Фиксация не определена или не входит в список фиксаций источника.",
                    parsed);

            default:
                break;
        }

        // Шаг 5: порог confidence — ниже порога только модерация.
        if (parsed.Confidence < src.ConfidenceThreshold)
        {
            return Moderate(
                src,
                rawMessageId,
                ModerationKind.LowConfidence,
                $"Уверенность {parsed.Confidence:0.###} ниже порога {src.ConfidenceThreshold:0.###}.",
                parsed);
        }

        // Шаг 6: режим «по тексту» при нескольких фиксациях — одна найденная; иначе — все фиксации.
        IReadOnlyList<ObservationProposed> observations;
        if (src.LinkByText && multipleObjects)
        {
            if (!Guid.TryParse(parsed.ObjectId, out var mapObjectId) ||
                !src.LinkedObjects.Any(linkedObject => linkedObject.Id == mapObjectId))
            {
                // Валидатор уже отклонил такой ответ; ветка — страховка от рассогласования.
                return Moderate(
                    src,
                    rawMessageId,
                    ModerationKind.ObjectNotFound,
                    "Фиксация не определена или не входит в список фиксаций источника.",
                    parsed);
            }

            observations =
            [
                new ObservationProposed(
                    src.SourceId,
                    mapObjectId,
                    parsed.Status ?? string.Empty,
                    rawMessageId,
                    msg.PostedAt,
                    parsed.Confidence),
            ];
        }
        else
        {
            observations = src.LinkedObjects
                .Select(linkedObject => new ObservationProposed(
                    src.SourceId,
                    linkedObject.Id,
                    parsed.Status ?? string.Empty,
                    rawMessageId,
                    msg.PostedAt,
                    parsed.Confidence))
                .ToList();
        }

        return new PipelineDecision.Propose(observations);
    }

    /// <summary>Собирает карточку модерации; предложение LLM сохраняется, если оно было.</summary>
    private static PipelineDecision Moderate(
        SourceRuntimeSnapshot src,
        Guid rawMessageId,
        ModerationKind kind,
        string reason,
        ParsedLlmResponse? response = null)
    {
        Guid? proposedMapObjectId = Guid.TryParse(response?.ObjectId, out var objectId) ? objectId : null;
        var evidenceQuote = string.IsNullOrEmpty(response?.EvidenceQuote) ? null : response.EvidenceQuote;

        var request = new ModerationCaseRequested(
            src.SourceId,
            rawMessageId,
            kind,
            reason,
            response?.Status,
            proposedMapObjectId,
            response?.Confidence,
            evidenceQuote);

        return new PipelineDecision.Moderate(request);
    }
}
