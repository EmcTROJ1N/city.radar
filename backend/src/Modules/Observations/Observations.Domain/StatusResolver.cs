namespace Observations.Domain;

/// <summary>
/// Доменный сервис расчёта итогового статуса фиксации.
/// Побеждает самая свежая актуальная оценка: наблюдения источников и пользователей
/// равнозначны, приоритетов и весов нет. Победитель определяется по
/// <see cref="ObservationFact.ObservedAt"/>, при равенстве — по <see cref="ObservationFact.ReceivedAt"/>.
/// </summary>
/// <param name="freshRatio">
/// Доля таймаута источника, до которой наблюдение считается <see cref="Freshness.Fresh"/>
/// (по умолчанию 0.5; граница включается в <see cref="Freshness.Aging"/>).
/// </param>
public sealed class StatusResolver(double freshRatio = 0.5)
{
    /// <summary>
    /// Учитывает только неотозванные наблюдения с <see cref="ObservationFact.ExpiresAt"/> &gt; <paramref name="now"/>
    /// и возвращает итоговый статус, свежесть и количество актуальных наблюдений.
    /// Если актуальных наблюдений нет — статус «неизвестно» (<c>null</c>) и <see cref="Freshness.Stale"/>.
    /// </summary>
    /// <param name="facts">Наблюдения фиксации; протухшие и отозванные отбрасываются.</param>
    /// <param name="now">Текущее серверное время.</param>
    /// <returns>Проекция итогового статуса фиксации.</returns>
    public CurrentStatusView Resolve(IEnumerable<ObservationFact> facts, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var actual = facts
            .Where(fact => !fact.IsRetracted && fact.ExpiresAt > now)
            .ToList();

        if (actual.Count == 0)
        {
            return new CurrentStatusView(
                SourceStatusId: null,
                SourceId: null,
                ObservationId: null,
                ObservedAt: null,
                Freshness: Freshness.Stale,
                Kind: null,
                ObservationCount: 0);
        }

        var winner = actual
            .OrderByDescending(fact => fact.ObservedAt)
            .ThenByDescending(fact => fact.ReceivedAt)
            .First();

        var age = now - winner.ObservedAt;
        var freshUntil = winner.SourceTimeout * freshRatio;
        var freshness = age < freshUntil ? Freshness.Fresh : Freshness.Aging;

        return new CurrentStatusView(
            SourceStatusId: winner.SourceStatusId,
            SourceId: winner.SourceId,
            ObservationId: winner.Id,
            ObservedAt: winner.ObservedAt,
            Freshness: freshness,
            Kind: winner.Kind,
            ObservationCount: actual.Count);
    }
}
