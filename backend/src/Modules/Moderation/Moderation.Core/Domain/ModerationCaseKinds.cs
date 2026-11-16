namespace Moderation.Core.Domain;

/// <summary>
/// Виды кейсов очереди модерации — значения check-ограничения <c>moderation_cases.kind</c>
/// из <c>db/schema.sql</c>.
/// </summary>
internal static class ModerationCaseKinds
{
    public const string LowConfidence = "low_confidence";
    public const string InvalidStatus = "invalid_status";
    public const string ObjectNotFound = "object_not_found";
    public const string InvalidResponse = "invalid_response";
    public const string Anomaly = "anomaly";
    public const string Complaint = "complaint";

    private static readonly string[] AllKinds =
    [
        LowConfidence,
        InvalidStatus,
        ObjectNotFound,
        InvalidResponse,
        Anomaly,
        Complaint,
    ];

    /// <summary>Входит ли значение в список, разрешённый схемой БД.</summary>
    public static bool IsKnown(string? kind) =>
        kind is not null && Array.IndexOf(AllKinds, kind) >= 0;
}

/// <summary>
/// Состояния кейса — значения check-ограничения <c>moderation_cases.state</c>
/// из <c>db/schema.sql</c>.
/// </summary>
internal static class ModerationCaseStates
{
    public const string Open = "open";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Dismissed = "dismissed";

    private static readonly string[] AllStates =
    [
        Open,
        Approved,
        Rejected,
        Dismissed,
    ];

    /// <summary>Входит ли значение в список, разрешённый схемой БД.</summary>
    public static bool IsKnown(string? state) =>
        state is not null && Array.IndexOf(AllStates, state) >= 0;
}
