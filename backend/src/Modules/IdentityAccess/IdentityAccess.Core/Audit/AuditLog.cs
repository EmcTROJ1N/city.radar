using System.Net;
using System.Text.Json;

using BuildingBlocks.Persistence;

using IdentityAccess.Contracts;
using IdentityAccess.Core.Domain;

namespace IdentityAccess.Core.Audit;

/// <summary>
/// Реализация <see cref="IAuditLog"/>: актор, IP и User-Agent берутся из <see cref="ICurrentUser"/>,
/// «до/после» сериализуются в jsonb (System.Text.Json, camelCase). Запись добавляется в тот же
/// scoped <see cref="MayakDbContext"/>, поэтому сохраняется одним <c>SaveChanges</c> вместе с
/// бизнес-изменением вызывающего кода и откатывается вместе с ним.
/// </summary>
internal sealed class AuditLog(MayakDbContext db, ICurrentUser currentUser, TimeProvider time) : IAuditLog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task WriteAsync(
        string action,
        string? entityType,
        string? entityId,
        object? before,
        object? after,
        string? reason = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ct.ThrowIfCancellationRequested();

        db.Add(new AuditEntry
        {
            ActorId = currentUser.UserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Before = Serialize(before),
            After = Serialize(after),
            Reason = reason,
            Ip = ParseIp(currentUser.Ip),
            UserAgent = currentUser.UserAgent,
            At = time.GetUtcNow(),
        });

        // SaveChanges не вызывается: запись уходит в БД вместе с изменением вызывающего кода.
        return Task.CompletedTask;
    }

    private static string? Serialize(object? value) =>
        value is null
            ? null
            : JsonSerializer.Serialize(value, value.GetType(), JsonOptions);

    /// <summary><c>inet</c>-колонка не принимает произвольный текст: нераспознанный IP пишем как <c>null</c>.</summary>
    private static IPAddress? ParseIp(string? ip) =>
        !string.IsNullOrWhiteSpace(ip) && IPAddress.TryParse(ip, out var address) ? address : null;
}
