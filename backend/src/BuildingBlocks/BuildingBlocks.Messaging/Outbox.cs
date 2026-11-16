using System.Text.Json;

using BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.Messaging;

/// <summary>
/// Реализация <see cref="IOutbox"/> поверх текущего scoped <see cref="MayakDbContext"/>:
/// запись добавляется в change tracker и сохраняется тем же <c>SaveChanges</c>, что и
/// бизнес-данные.
/// </summary>
public sealed class Outbox(MayakDbContext db, TimeProvider timeProvider) : IOutbox
{
    /// <inheritdoc />
    public void Enqueue(string destination, string type, object payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(payload);

        var now = timeProvider.GetUtcNow();

        db.Set<OutboxMessage>().Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Destination = destination,
            Type = type,
            // Web-настройки — camelCase, как в контракте API (api/openapi.yaml).
            Payload = JsonSerializer.Serialize(payload, payload.GetType(), JsonSerializerOptions.Web),
            CreatedAt = now,
            AvailableAt = now,
        });
    }
}
