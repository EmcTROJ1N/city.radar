namespace Observations.Domain;

/// <summary>
/// Итоговый статус фиксации — результат работы <see cref="StatusResolver"/> и содержимое
/// проекции <c>current_statuses</c>. Карта читает только эту проекцию.
/// </summary>
/// <param name="SourceStatusId">Статус победившего наблюдения; <c>null</c>, если актуальных наблюдений нет.</param>
/// <param name="SourceId">Источник победившего наблюдения.</param>
/// <param name="ObservationId">Победившее наблюдение.</param>
/// <param name="ObservedAt">Время победившего наблюдения.</param>
/// <param name="Freshness">Свежесть победившего наблюдения.</param>
/// <param name="Kind">Тип источника победившего наблюдения.</param>
/// <param name="ObservationCount">Количество учтённых актуальных наблюдений.</param>
public sealed record CurrentStatusView(
    Guid? SourceStatusId,
    Guid? SourceId,
    Guid? ObservationId,
    DateTimeOffset? ObservedAt,
    Freshness Freshness,
    SourceKind? Kind,
    int ObservationCount);
