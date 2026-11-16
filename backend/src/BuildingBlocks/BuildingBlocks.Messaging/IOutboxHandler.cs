using System.Text.Json;

namespace BuildingBlocks.Messaging;

/// <summary>
/// Обработчик сообщения transactional outbox. Диспетчер находит обработчик
/// по <see cref="Type"/> и вызывает его внутри scope пачки.
/// </summary>
/// <remarks>
/// Обработчики обязаны быть идемпотентными: доставка at-least-once, ключ идемпотентности —
/// <c>outbox_messages.id</c>, который передаётся в <see cref="HandleAsync"/>. Повторная доставка
/// того же сообщения не должна создавать второй результат.
/// </remarks>
public interface IOutboxHandler
{
    /// <summary>Тип сообщения, например <c>ObservationProposed</c> или <c>ModerationCaseRequested</c>.</summary>
    string Type { get; }

    /// <summary>
    /// Обрабатывает сообщение. <paramref name="messageId"/> — <c>outbox_messages.id</c>; используется
    /// как ключ идемпотентности (уникальность создаваемой сущности, проверка «уже обработано»).
    /// Исключение означает ошибку обработки: диспетчер увеличит <c>attempts</c>, запишет
    /// <c>last_error</c> и отложит повтор.
    /// </summary>
    Task HandleAsync(JsonElement payload, Guid messageId, CancellationToken cancellationToken);
}
