namespace Observations.Domain;

/// <summary>
/// Доменный сервис расчёта итогового статуса фиксации.
/// Для точки побеждает самая свежая актуальная оценка (<see cref="Resolve"/>), для зоны
/// считается сводка по последним отметкам (<see cref="ResolveZone"/>). Наблюдения источников
/// и пользователей равнозначны, приоритетов и весов нет. Свежесть определяется по
/// <see cref="ObservationFact.ObservedAt"/>, при равенстве — по <see cref="ObservationFact.ReceivedAt"/>.
/// </summary>
/// <param name="freshRatio">
/// Доля таймаута источника, до которой наблюдение считается <see cref="Freshness.Fresh"/>
/// (по умолчанию 0.5; граница включается в <see cref="Freshness.Aging"/>).
/// </param>
public sealed class StatusResolver(double freshRatio = 0.5)
{
    /// <summary>Окно сводки по зоне, отсчитываемое от самой свежей актуальной отметки.</summary>
    public static readonly TimeSpan ZoneWindow = TimeSpan.FromHours(1);

    /// <summary>Доля отметок «есть» в окне, начиная с которой зона считается благополучной.</summary>
    public const double ZonePositiveShare = 0.8;

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

        var winner = Latest(actual);

        return View(winner, winner.SourceStatusId, actual.Count, now);
    }

    /// <summary>
    /// Сводка по зоне: в отметках зоны пишут про разные дома, поэтому «последняя побеждает» дала бы
    /// мигающий статус. Учитываются актуальные отметки за <see cref="ZoneWindow"/> до самой свежей:
    /// все «нет» → «нет»; доля «есть» не меньше <see cref="ZonePositiveShare"/> → «есть»;
    /// иначе — общий статус, если у всех он одинаковый, или «частичный» статус источника
    /// (<see cref="ObservationFact.PartialStatusId"/>). Свежесть и время — по самой свежей отметке.
    /// </summary>
    /// <param name="facts">Наблюдения зоны; протухшие и отозванные отбрасываются.</param>
    /// <param name="now">Текущее серверное время.</param>
    /// <returns>Проекция итогового статуса зоны; <see cref="CurrentStatusView.ObservationCount"/> — отметок в окне.</returns>
    public CurrentStatusView ResolveZone(IEnumerable<ObservationFact> facts, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var actual = facts
            .Where(fact => !fact.IsRetracted && fact.ExpiresAt > now)
            .ToList();

        if (actual.Count == 0)
        {
            return Resolve(actual, now);
        }

        var latest = Latest(actual);
        var window = actual
            .Where(fact => fact.ObservedAt > latest.ObservedAt - ZoneWindow)
            .ToList();

        if (window.All(fact => fact.Tone == StatusTone.Negative))
        {
            return View(latest, latest.SourceStatusId, window.Count, now);
        }

        var positives = window.Where(fact => fact.Tone == StatusTone.Positive).ToList();
        if (positives.Count >= window.Count * ZonePositiveShare)
        {
            var winner = Latest(positives);
            return View(winner, winner.SourceStatusId, window.Count, now);
        }

        if (window.All(fact => fact.SourceStatusId == latest.SourceStatusId))
        {
            return View(latest, latest.SourceStatusId, window.Count, now);
        }

        // Смешанная картина. Если у источника нет «частичного» статуса, лучше показать самую
        // свежую «ограниченную» отметку, чем ничего; статус и источник берутся из одной отметки.
        if (latest.PartialStatusId is { } partialStatusId)
        {
            return View(latest, partialStatusId, window.Count, now);
        }

        var limited = window.Where(fact => fact.Tone == StatusTone.Limited).ToList();
        var fallback = limited.Count > 0 ? Latest(limited) : latest;

        return View(fallback, fallback.SourceStatusId, window.Count, now);
    }

    private static ObservationFact Latest(IEnumerable<ObservationFact> facts) => facts
        .OrderByDescending(fact => fact.ObservedAt)
        .ThenByDescending(fact => fact.ReceivedAt)
        .First();

    private CurrentStatusView View(ObservationFact winner, Guid sourceStatusId, int count, DateTimeOffset now)
    {
        var age = now - winner.ObservedAt;
        var freshUntil = winner.SourceTimeout * freshRatio;
        var freshness = age < freshUntil ? Freshness.Fresh : Freshness.Aging;

        return new CurrentStatusView(
            SourceStatusId: sourceStatusId,
            SourceId: winner.SourceId,
            ObservationId: winner.Id,
            ObservedAt: winner.ObservedAt,
            Freshness: freshness,
            Kind: winner.Kind,
            ObservationCount: count);
    }
}
