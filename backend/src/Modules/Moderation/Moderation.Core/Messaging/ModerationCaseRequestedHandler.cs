using System.Text.Json;

using BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;

using Moderation.Contracts;
using Moderation.Core.Domain;
using Moderation.Core.Services;

namespace Moderation.Core.Messaging;

/// <summary>
/// Обработчик outbox-сообщения <c>ModerationCaseRequested</c> (Worker → Api, destination
/// <c>api</c>): создаёт кейс очереди модерации из предложения воркера.
/// </summary>
/// <remarks>
/// <para>
/// Формат payload (имена полей camelCase, значения enum — строки):
/// <c>{ sourceId, rawMessageId, kind, reason, proposedStatusCode, proposedMapObjectId,
/// confidence, evidenceQuote }</c>. Поля предложения складываются в
/// <c>moderation_cases.proposed</c>: <c>{ statusCode, mapObjectId, confidence, evidenceQuote,
/// reason }</c>.
/// </para>
/// <para>
/// Обработчик идемпотентен по <c>messageId</c>: ключ хранится в
/// <c>moderation_cases.outbox_message_id</c> (уникальный), поэтому повторная доставка того же
/// сообщения не создаёт второй кейс. <c>SaveChanges</c> вызывается здесь: диспетчер не сохраняет
/// изменения обработчика сам, а фиксирует их вместе с <c>processed_at</c> в одной транзакции.
/// </para>
/// </remarks>
internal sealed class ModerationCaseRequestedHandler(
    MayakDbContext db,
    ModerationCaseCreator creator) : BuildingBlocks.Messaging.IOutboxHandler
{
    /// <summary>Тип сообщения, который обрабатывает этот класс.</summary>
    public const string MessageType = "ModerationCaseRequested";

    /// <inheritdoc />
    public string Type => MessageType;

    /// <inheritdoc />
    public async Task HandleAsync(
        JsonElement payload,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Идемпотентность: сообщение уже обработано — второй кейс не создаём.
        var alreadyProcessed = await db.Set<ModerationCase>()
            .AnyAsync(
                moderationCase => moderationCase.OutboxMessageId == messageId,
                cancellationToken);
        if (alreadyProcessed)
        {
            return;
        }

        var kind = ReadString(payload, "kind")
            ?? throw new ArgumentException(
                "В сообщении ModerationCaseRequested отсутствует kind.",
                nameof(payload));
        if (!ModerationCaseKinds.IsKnown(kind))
        {
            throw new ArgumentException($"Неизвестный вид кейса: {kind}", nameof(payload));
        }

        var proposedMapObjectId = ReadGuid(payload, "proposedMapObjectId");
        var proposed = new
        {
            statusCode = ReadString(payload, "proposedStatusCode"),
            mapObjectId = proposedMapObjectId,
            confidence = ReadNumber(payload, "confidence"),
            evidenceQuote = ReadString(payload, "evidenceQuote"),
            reason = ReadString(payload, "reason"),
            address = ReadString(payload, "address"),
        };

        var input = new NewModerationCase(
            kind,
            ReadGuid(payload, "sourceId"),
            ReadGuid(payload, "rawMessageId"),
            proposedMapObjectId,
            ObservationId: null,
            ReporterId: null,
            Comment: null,
            Proposed: proposed);

        await creator.CreateAsync(input, messageId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string? ReadString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Guid? ReadGuid(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.TryGetGuid(out var id)
            ? id
            : null;

    private static double? ReadNumber(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
