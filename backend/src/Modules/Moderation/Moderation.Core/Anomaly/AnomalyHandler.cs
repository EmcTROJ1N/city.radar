using System.Text.Json;

using BuildingBlocks.Messaging;

namespace Moderation.Core.Anomaly;

/// <summary>
/// Обработчик outbox-события <c>ObservationCreated</c>: передаёт фиксацию детектору
/// аномалий. Событие кладёт <c>CreateObservationHandler</c> в той же транзакции, что и отметку.
/// </summary>
/// <remarks>
/// Payload: <c>{ observationId, mapObjectId, groupId, sourceKind, at }</c>. Наблюдения
/// модераторов (<c>sourceKind = "moderator"</c>) аномалию не запускают — они не считаются в
/// детекторе. Окно проверяется на момент обработки сообщения (<see cref="TimeProvider"/>),
/// а не по <c>at</c> из payload: повторная доставка не должна «состарить» окно.
/// </remarks>
public sealed class AnomalyHandler(AnomalyDetector detector, TimeProvider timeProvider) : IOutboxHandler
{
    /// <summary>Тип сообщения, который обрабатывает этот класс.</summary>
    public const string MessageType = "ObservationCreated";

    /// <inheritdoc />
    public string Type => MessageType;

    /// <inheritdoc />
    public async Task HandleAsync(
        JsonElement payload,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var mapObjectId = ReadGuid(payload, "mapObjectId")
            ?? throw new ArgumentException(
                "В сообщении ObservationCreated отсутствует mapObjectId.",
                nameof(payload));

        var sourceKind = ReadString(payload, "sourceKind");
        if (sourceKind is not null && !string.Equals(sourceKind, "manual", StringComparison.Ordinal))
        {
            return;
        }

        await detector.CheckAsync(
            ReadGuid(payload, "groupId"),
            mapObjectId,
            timeProvider.GetUtcNow(),
            cancellationToken);
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
}
