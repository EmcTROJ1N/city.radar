using System.Security.Cryptography;
using System.Text.Json;

using BuildingBlocks.Messaging;
using BuildingBlocks.Persistence;

using Catalog.Contracts;

using Microsoft.Extensions.Logging;

using Observations.Domain;

using Sources.Contracts;

namespace Observations.Application.Handlers;

/// <summary>
/// Обработчик outbox-сообщения <c>ObservationProposed</c> (Worker → Api, destination
/// <c>api</c>): принимает готовое предложение Worker-а,
/// создаёт наблюдение, пересчитывает проекцию и делает это идемпотентно.
/// </summary>
/// <remarks>
/// <para>
/// Формат payload (camelCase): <c>{ sourceId, mapObjectId, statusCode, rawMessageId,
/// observedAt, confidence }</c>. <c>observedAt</c> — время сообщения
/// (<c>raw_messages.posted_at</c>), поэтому старые сообщения сохраняются с уже истёкшим
/// <c>ExpiresAt</c> и не влияют на карту.
/// </para>
/// <para>
/// Идемпотентность — детерминированный <c>Guid</c> наблюдения из
/// <c>(messageId, mapObjectId)</c>: повторная доставка того же сообщения находит уже
/// созданное наблюдение и пропускается. Дополнительных колонок/таблиц не требуется.
/// Одно сообщение для двух фиксаций — это два разных mapObjectId (и два наблюдения), а
/// повтор каждой из них не создаёт дублей.
/// </para>
/// <para>
/// Пропуск без ошибки (сообщение помечается обработанным): источник выключен или удалён,
/// фиксация неактивна/удалена/не привязана к источнику, статус не найден среди статусов
/// источника. Ошибка в таких случаях означала бы бесконечный повтор «отравленного» сообщения.
/// </para>
/// </remarks>
public sealed class ObservationProposedHandler(
    MayakDbContext db,
    IObservationRepository repository,
    IStatusRecalculator recalculator,
    IMapObjectDirectory mapObjects,
    ISourceDirectory sources,
    TimeProvider time,
    ILogger<ObservationProposedHandler> logger) : IOutboxHandler
{
    /// <summary>Тип сообщения, который обрабатывает этот класс.</summary>
    public const string MessageType = "ObservationProposed";

    /// <inheritdoc />
    public string Type => MessageType;

    /// <inheritdoc />
    public async Task HandleAsync(
        JsonElement payload,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sourceId = ReadRequiredGuid(payload, "sourceId");
        var mapObjectId = ReadRequiredGuid(payload, "mapObjectId");
        var rawMessageId = ReadRequiredGuid(payload, "rawMessageId");
        var statusCode = ReadRequiredString(payload, "statusCode");
        var observedAt = ReadRequiredTimestamp(payload, "observedAt");
        var confidence = ReadOptionalNumber(payload, "confidence");
        var address = ReadOptionalString(payload, "address");

        // Ключ идемпотентности: одно сообщение + одна фиксация = одно наблюдение.
        var observationId = DeterministicId(messageId, mapObjectId);
        if (await repository.GetAsync(observationId, cancellationToken) is not null)
        {
            logger.LogInformation(
                "ObservationProposed {MessageId}: наблюдение {ObservationId} уже создано, повтор пропущен.",
                messageId,
                observationId);

            return;
        }

        if (!await sources.IsEnabledAsync(sourceId, cancellationToken))
        {
            logger.LogInformation(
                "ObservationProposed {MessageId}: источник {SourceId} выключен или удалён, сообщение пропущено.",
                messageId,
                sourceId);

            return;
        }

        var policy = await sources.GetPolicyAsync(sourceId, cancellationToken);
        if (policy is null)
        {
            logger.LogInformation(
                "ObservationProposed {MessageId}: политика источника {SourceId} не найдена, сообщение пропущено.",
                messageId,
                sourceId);

            return;
        }

        var info = await mapObjects.GetInfoAsync(mapObjectId, cancellationToken);
        if (info is null || !info.IsActive)
        {
            logger.LogInformation(
                "ObservationProposed {MessageId}: фиксация {MapObjectId} удалена или неактивна, сообщение пропущено.",
                messageId,
                mapObjectId);

            return;
        }

        var linkedSources = await mapObjects.GetSourceIdsAsync(mapObjectId, cancellationToken);
        if (!linkedSources.Contains(sourceId))
        {
            logger.LogInformation(
                "ObservationProposed {MessageId}: источник {SourceId} не привязан к фиксации {MapObjectId}, сообщение пропущено.",
                messageId,
                sourceId,
                mapObjectId);

            return;
        }

        var statuses = await sources.GetStatusesAsync(sourceId, cancellationToken);
        var status = statuses.FirstOrDefault(
            candidate => string.Equals(candidate.Code, statusCode, StringComparison.Ordinal));
        if (status is null)
        {
            // Worker уже отправил такой ответ в модерацию; здесь кейс не создаётся.
            logger.LogWarning(
                "ObservationProposed {MessageId}: статус '{StatusCode}' не принадлежит источнику {SourceId}, сообщение пропущено.",
                messageId,
                statusCode,
                sourceId);

            return;
        }

        if (!TryMapKind(policy.SourceType, out var kind))
        {
            logger.LogWarning(
                "ObservationProposed {MessageId}: неизвестный тип источника '{SourceType}', сообщение пропущено.",
                messageId,
                policy.SourceType);

            return;
        }

        var now = time.GetUtcNow();

        Observation observation;
        try
        {
            observation = Observation.Create(
                new ObservationDraft(
                    mapObjectId,
                    sourceId,
                    status.Id,
                    kind,
                    ObservedAt: observedAt,
                    ReceivedAt: now,
                    ReporterId: null,
                    Confidence: confidence,
                    RawPayloadRef: rawMessageId.ToString(),
                    Comment: null,
                    Address: address),
                policy.Timeout,
                now,
                observationId,
                // Сообщение могло прийти позже таймаута: сохраняем для истории, статус не меняем.
                allowExpired: true);
        }
        catch (DomainException exception)
        {
            logger.LogWarning(
                "ObservationProposed {MessageId}: наблюдение не создано ({Reason}).",
                messageId,
                exception.Message);

            return;
        }

        await repository.AddAsync(observation, cancellationToken);

        // Факты для резолвера читаются из таблицы: сохраняем наблюдение до пересчёта.
        // Транзакция диспетчера/вызывающего при этом остаётся одна.
        await db.SaveChangesAsync(cancellationToken);

        await recalculator.RecalculateAsync(mapObjectId, now, cancellationToken);

        logger.LogInformation(
            "ObservationProposed {MessageId}: создано наблюдение {ObservationId} для фиксации {MapObjectId}.",
            messageId,
            observationId,
            mapObjectId);
    }

    /// <summary>
    /// Идентификатор наблюдения из пары «сообщение outbox + фиксация». Стабилен между
    /// доставками и различается для двух фиксаций одного сообщения.
    /// </summary>
    private static Guid DeterministicId(Guid messageId, Guid mapObjectId)
    {
        Span<byte> input = stackalloc byte[32];
        messageId.TryWriteBytes(input);
        mapObjectId.TryWriteBytes(input[16..]);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);

        return new Guid(hash[..16]);
    }

    private static bool TryMapKind(string sourceType, out SourceKind kind)
    {
        switch (sourceType)
        {
            case "telegram_chat":
                kind = SourceKind.Telegram;

                return true;
            case "max_chat":
                kind = SourceKind.Max;

                return true;
            default:
                kind = default;

                return false;
        }
    }

    private static Guid ReadRequiredGuid(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.TryGetGuid(out var id)
            ? id
            : throw new ArgumentException(
                $"В сообщении ObservationProposed отсутствует корректный {name}.",
                nameof(payload));

    private static string ReadRequiredString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException(
                $"В сообщении ObservationProposed отсутствует {name}.",
                nameof(payload));

    private static DateTimeOffset ReadRequiredTimestamp(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.TryGetDateTimeOffset(out var timestamp)
            ? timestamp
            : throw new ArgumentException(
                $"В сообщении ObservationProposed отсутствует корректный {name}.",
                nameof(payload));

    private static string? ReadOptionalString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static float? ReadOptionalNumber(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? (float)number
            : null;
}
