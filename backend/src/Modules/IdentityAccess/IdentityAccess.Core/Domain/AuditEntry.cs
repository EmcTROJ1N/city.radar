using System.Net;

namespace IdentityAccess.Core.Domain;

/// <summary>
/// Запись журнала аудита (таблица <c>audit_entries</c>). Журнал append-only:
/// триггер <c>audit_entries_append_only</c> запрещает UPDATE и DELETE.
/// </summary>
internal sealed class AuditEntry
{
    public long Id { get; set; }

    public Guid? ActorId { get; set; }

    public string Action { get; set; } = null!;

    public string? EntityType { get; set; }

    public string? EntityId { get; set; }

    /// <summary>Состояние «до» — jsonb.</summary>
    public string? Before { get; set; }

    /// <summary>Состояние «после» — jsonb.</summary>
    public string? After { get; set; }

    public string? Reason { get; set; }

    /// <summary>Адрес клиента — inet.</summary>
    public IPAddress? Ip { get; set; }

    public string? UserAgent { get; set; }

    public DateTimeOffset At { get; set; }
}
