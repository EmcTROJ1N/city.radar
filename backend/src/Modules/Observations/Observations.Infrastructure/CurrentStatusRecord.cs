namespace Observations.Infrastructure;

/// <summary>
/// Строка проекции <c>current_statuses</c>: одна на фиксацию, карта читает только
/// её. Модель нужна для схемы и проверок; запись строки идёт SQL-upsert-ом в
/// <see cref="CurrentStatusWriter"/>, потому что <c>updated_at</c> обновляется
/// выборочно — только при изменении итога.
/// </summary>
internal sealed class CurrentStatusRecord
{
    public Guid MapObjectId { get; set; }

    public Guid? SourceStatusId { get; set; }

    public Guid? SourceId { get; set; }

    public Guid? ObservationId { get; set; }

    public DateTimeOffset? ObservedAt { get; set; }

    /// <summary>Свежесть в БД: <c>fresh</c>, <c>aging</c> или <c>stale</c>.</summary>
    public string Freshness { get; set; } = "stale";

    public string? SourceType { get; set; }

    public int ObservationCount { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
