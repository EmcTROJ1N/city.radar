using Shouldly;

namespace Observations.Domain.Tests;

/// <summary>
/// Модульные тесты <see cref="StatusResolver"/> и <see cref="Observation.Create"/>:
/// побеждает самая свежая актуальная оценка, протухшие и отозванные наблюдения не учитываются,
/// приоритета у модератора нет.
/// </summary>
public sealed class StatusResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Timeout = TimeSpan.FromHours(4);

    [Fact]
    public void Latest_observed_at_wins_across_sources_and_users()
    {
        var olderSourceId = Guid.NewGuid();
        var olderStatusId = Guid.NewGuid();
        var newerSourceId = Guid.NewGuid();
        var newerStatusId = Guid.NewGuid();
        var newerObservationId = Guid.NewGuid();

        var facts = new[]
        {
            Fact(
                Now.AddMinutes(-30),
                sourceId: olderSourceId,
                sourceStatusId: olderStatusId,
                kind: SourceKind.Telegram),
            Fact(
                Now.AddMinutes(-5),
                sourceId: newerSourceId,
                sourceStatusId: newerStatusId,
                kind: SourceKind.Manual,
                id: newerObservationId),
        };

        var view = new StatusResolver().Resolve(facts, Now);

        view.SourceStatusId.ShouldBe(newerStatusId);
        view.SourceId.ShouldBe(newerSourceId);
        view.ObservationId.ShouldBe(newerObservationId);
        view.ObservedAt.ShouldBe(Now.AddMinutes(-5));
        view.Kind.ShouldBe(SourceKind.Manual);
        view.ObservationCount.ShouldBe(2);
    }

    [Fact]
    public void Tie_on_observed_at_resolved_by_received_at()
    {
        var observedAt = Now.AddMinutes(-10);
        var earlierReceivedId = Guid.NewGuid();
        var laterReceivedId = Guid.NewGuid();

        var facts = new[]
        {
            Fact(
                observedAt,
                receivedAt: observedAt.AddSeconds(5),
                kind: SourceKind.Manual,
                id: earlierReceivedId),
            Fact(
                observedAt,
                receivedAt: observedAt.AddSeconds(30),
                kind: SourceKind.Telegram,
                id: laterReceivedId),
        };

        var view = new StatusResolver().Resolve(facts, Now);

        view.ObservationId.ShouldBe(laterReceivedId);
        view.Kind.ShouldBe(SourceKind.Telegram);
    }

    [Fact]
    public void Expired_observation_is_ignored()
    {
        var actualSourceId = Guid.NewGuid();
        var actualStatusId = Guid.NewGuid();

        var facts = new[]
        {
            Fact(
                Now.AddMinutes(-1),
                sourceTimeout: TimeSpan.FromMinutes(1),
                expiresAt: Now,
                kind: SourceKind.Max,
                sourceId: Guid.NewGuid(),
                sourceStatusId: Guid.NewGuid()),
            Fact(
                Now.AddHours(-3),
                sourceId: actualSourceId,
                sourceStatusId: actualStatusId,
                kind: SourceKind.Telegram),
        };

        var view = new StatusResolver().Resolve(facts, Now);

        view.SourceStatusId.ShouldBe(actualStatusId);
        view.SourceId.ShouldBe(actualSourceId);
        view.ObservationCount.ShouldBe(1);
    }

    [Fact]
    public void Retracted_observation_is_ignored()
    {
        var actualSourceId = Guid.NewGuid();
        var actualStatusId = Guid.NewGuid();

        var facts = new[]
        {
            Fact(
                Now.AddMinutes(-2),
                isRetracted: true,
                sourceId: Guid.NewGuid(),
                sourceStatusId: Guid.NewGuid()),
            Fact(
                Now.AddHours(-2),
                sourceId: actualSourceId,
                sourceStatusId: actualStatusId),
        };

        var view = new StatusResolver().Resolve(facts, Now);

        view.SourceStatusId.ShouldBe(actualStatusId);
        view.SourceId.ShouldBe(actualSourceId);
        view.ObservationCount.ShouldBe(1);
    }

    [Fact]
    public void No_actual_observations_gives_unknown_and_stale()
    {
        var facts = new[]
        {
            Fact(Now.AddHours(-5), sourceTimeout: TimeSpan.FromHours(1)),
            Fact(Now.AddMinutes(-1), isRetracted: true),
        };

        var view = new StatusResolver().Resolve(facts, Now);

        view.SourceStatusId.ShouldBeNull();
        view.SourceId.ShouldBeNull();
        view.ObservationId.ShouldBeNull();
        view.ObservedAt.ShouldBeNull();
        view.Freshness.ShouldBe(Freshness.Stale);
        view.Kind.ShouldBeNull();
        view.ObservationCount.ShouldBe(0);
    }

    [Fact]
    public void Empty_facts_give_unknown_and_stale()
    {
        var view = new StatusResolver().Resolve([], Now);

        view.ShouldBe(new CurrentStatusView(null, null, null, null, Freshness.Stale, null, 0));
    }

    [Fact]
    public void Freshness_is_fresh_before_half_of_timeout_aging_after_until_expiry()
    {
        var resolver = new StatusResolver();

        // Чуть меньше половины таймаута — Fresh.
        resolver.Resolve([Fact(Now.AddHours(-2).AddSeconds(1))], Now)
            .Freshness.ShouldBe(Freshness.Fresh);

        // Ровно 50% таймаута — уже Aging (граница включительно).
        resolver.Resolve([Fact(Now.AddHours(-2))], Now)
            .Freshness.ShouldBe(Freshness.Aging);

        // Перед истечением — Aging.
        resolver.Resolve([Fact(Now.AddHours(-4).AddSeconds(1))], Now)
            .Freshness.ShouldBe(Freshness.Aging);

        // Ровно ExpiresAt — наблюдение уже не актуально (условие ExpiresAt > now).
        var atExpiry = resolver.Resolve([Fact(Now.AddHours(-4))], Now);

        atExpiry.Freshness.ShouldBe(Freshness.Stale);
        atExpiry.SourceStatusId.ShouldBeNull();
    }

    [Fact]
    public void Freshness_uses_timeout_of_winning_observation_source()
    {
        var shortSourceId = Guid.NewGuid();
        var shortStatusId = Guid.NewGuid();

        var facts = new[]
        {
            // Победитель: полчаса назад, источник с таймаутом 1 час → Aging.
            Fact(
                Now.AddMinutes(-30),
                sourceTimeout: TimeSpan.FromHours(1),
                sourceId: shortSourceId,
                sourceStatusId: shortStatusId),
            // Не влияет: наблюдение старше, таймаут другого источника.
            Fact(Now.AddHours(-2), sourceTimeout: TimeSpan.FromHours(4)),
        };

        var view = new StatusResolver().Resolve(facts, Now);

        view.SourceId.ShouldBe(shortSourceId);
        view.SourceStatusId.ShouldBe(shortStatusId);
        view.Freshness.ShouldBe(Freshness.Aging);
    }

    [Fact]
    public void ObservationCount_counts_only_actual()
    {
        var facts = new[]
        {
            Fact(Now.AddMinutes(-5)),
            Fact(Now.AddMinutes(-15)),
            Fact(Now.AddMinutes(-20), isRetracted: true),
            Fact(Now.AddHours(-2), sourceTimeout: TimeSpan.FromMinutes(30)),
        };

        var view = new StatusResolver().Resolve(facts, Now);

        view.ObservationCount.ShouldBe(2);
    }

    [Fact]
    public void Moderator_observation_has_no_priority()
    {
        var userStatusId = Guid.NewGuid();

        var facts = new[]
        {
            // Модератор сделал отметку раньше — она проигрывает.
            Fact(Now.AddHours(-3), kind: SourceKind.Moderator),
            Fact(Now.AddHours(-1), kind: SourceKind.Manual, sourceStatusId: userStatusId),
        };

        var view = new StatusResolver().Resolve(facts, Now);

        view.SourceStatusId.ShouldBe(userStatusId);
        view.Kind.ShouldBe(SourceKind.Manual);
    }

    [Fact]
    public void Changing_source_timeout_does_not_change_existing_expires_at()
    {
        var observationId = Guid.NewGuid();

        var facts = new[]
        {
            // Таймаут источника в настройках теперь 10 часов, но ExpiresAt зафиксирован
            // при создании наблюдения и уже наступил — наблюдение не учитывается.
            Fact(
                Now.AddHours(-1),
                sourceTimeout: TimeSpan.FromHours(10),
                expiresAt: Now.AddSeconds(-1),
                id: observationId),
        };

        var view = new StatusResolver().Resolve(facts, Now);

        view.SourceStatusId.ShouldBeNull();
        view.ObservationCount.ShouldBe(0);
    }

    [Fact]
    public void Create_sets_expires_at_from_timeout()
    {
        var draft = new ObservationDraft(
            MapObjectId: Guid.NewGuid(),
            SourceId: Guid.NewGuid(),
            SourceStatusId: Guid.NewGuid(),
            Kind: SourceKind.Telegram,
            ObservedAt: Now.AddMinutes(-10),
            ReceivedAt: Now.AddMinutes(-9),
            ReporterId: null,
            Confidence: 0.87f,
            RawPayloadRef: "raw-message-1",
            Comment: null);

        var observation = Observation.Create(draft, Timeout, Now);

        observation.Id.ShouldNotBe(Guid.Empty);
        observation.MapObjectId.ShouldBe(draft.MapObjectId);
        observation.SourceId.ShouldBe(draft.SourceId);
        observation.SourceStatusId.ShouldBe(draft.SourceStatusId);
        observation.Kind.ShouldBe(draft.Kind);
        observation.ObservedAt.ShouldBe(draft.ObservedAt);
        observation.ReceivedAt.ShouldBe(draft.ReceivedAt);
        observation.ExpiresAt.ShouldBe(draft.ObservedAt + Timeout);
        observation.Confidence.ShouldBe(draft.Confidence);
        observation.RawPayloadRef.ShouldBe(draft.RawPayloadRef);
        observation.IsRetracted.ShouldBeFalse();
    }

    [Fact]
    public void Create_rejects_nonpositive_timeout()
    {
        var draft = Draft(Now.AddMinutes(-1));

        Should.Throw<DomainException>(() => Observation.Create(draft, TimeSpan.Zero, Now));
        Should.Throw<DomainException>(() => Observation.Create(draft, TimeSpan.FromSeconds(-1), Now));
    }

    [Fact]
    public void Create_rejects_far_future_observed_at()
    {
        var resolverTime = Now;

        Should.Throw<DomainException>(
            () => Observation.Create(Draft(Now.AddMinutes(6)), Timeout, resolverTime));

        // Допустимое расхождение часов — не более пяти минут вперёд.
        Should.NotThrow(
            () => Observation.Create(Draft(Now.AddMinutes(5)), Timeout, resolverTime));
    }

    [Fact]
    public void Create_rejects_expired_observation_by_default()
    {
        // ObservedAt на два часа раньше, таймаут один час: ExpiresAt уже в прошлом.
        var draft = Draft(Now.AddHours(-2));

        Should.Throw<DomainException>(
            () => Observation.Create(draft, TimeSpan.FromHours(1), Now));
    }

    [Fact]
    public void Create_allows_expired_observation_when_requested()
    {
        var draft = Draft(Now.AddHours(-2));

        var observation = Observation.Create(
            draft,
            TimeSpan.FromHours(1),
            Now,
            allowExpired: true);

        observation.ExpiresAt.ShouldBe(Now.AddHours(-1));
        observation.IsRetracted.ShouldBeFalse();

        // Протухшее наблюдение сохраняется, но проекция остаётся «неизвестно»/stale.
        var view = new StatusResolver().Resolve(
            [
                new ObservationFact(
                    observation.Id,
                    observation.SourceId,
                    observation.SourceStatusId,
                    observation.Kind,
                    observation.ObservedAt,
                    observation.ReceivedAt,
                    observation.ExpiresAt,
                    TimeSpan.FromHours(1),
                    IsRetracted: false),
            ],
            Now);

        view.SourceStatusId.ShouldBeNull();
        view.Freshness.ShouldBe(Freshness.Stale);
    }

    [Fact]
    public void Create_uses_explicit_id_for_idempotency()
    {
        var id = Guid.NewGuid();
        var draft = Draft(Now.AddMinutes(-1));

        var observation = Observation.Create(draft, Timeout, Now, id);

        observation.Id.ShouldBe(id);
    }

    [Fact]
    public void Configurable_fresh_ratio()
    {
        var resolver = new StatusResolver(freshRatio: 0.25);
        var observedAt = Now.AddHours(-1);

        // 25% от четырёх часов — один час; ровно граница уже Aging.
        resolver.Resolve([Fact(observedAt, sourceTimeout: Timeout)], Now)
            .Freshness.ShouldBe(Freshness.Aging);

        resolver.Resolve([Fact(observedAt.AddSeconds(1), sourceTimeout: Timeout)], Now)
            .Freshness.ShouldBe(Freshness.Fresh);
    }

    private static ObservationDraft Draft(DateTimeOffset observedAt) =>
        new(
            MapObjectId: Guid.NewGuid(),
            SourceId: Guid.NewGuid(),
            SourceStatusId: Guid.NewGuid(),
            Kind: SourceKind.Manual,
            ObservedAt: observedAt,
            ReceivedAt: observedAt);

    private static readonly Guid OkStatusId = Guid.NewGuid();
    private static readonly Guid NoneStatusId = Guid.NewGuid();
    private static readonly Guid WeakStatusId = Guid.NewGuid();
    private static readonly Guid PartialStatusId = Guid.NewGuid();

    private static ObservationFact ZoneFact(int minutesAgo, StatusTone tone, Guid statusId, Guid? partialStatusId = null) =>
        Fact(Now.AddMinutes(-minutesAgo), sourceStatusId: statusId, tone: tone, partialStatusId: partialStatusId);

    [Fact]
    public void Zone_without_water_everywhere_is_negative()
    {
        var facts = new[]
        {
            ZoneFact(30, StatusTone.Negative, NoneStatusId, PartialStatusId),
            ZoneFact(10, StatusTone.Negative, NoneStatusId, PartialStatusId),
        };

        var view = new StatusResolver().ResolveZone(facts, Now);

        view.SourceStatusId.ShouldBe(NoneStatusId);
        view.ObservedAt.ShouldBe(Now.AddMinutes(-10));
        view.ObservationCount.ShouldBe(2);
    }

    [Fact]
    public void Zone_with_overwhelming_majority_of_water_is_positive()
    {
        var facts = new[]
        {
            ZoneFact(40, StatusTone.Positive, OkStatusId, PartialStatusId),
            ZoneFact(30, StatusTone.Positive, OkStatusId, PartialStatusId),
            ZoneFact(20, StatusTone.Negative, NoneStatusId, PartialStatusId),
            ZoneFact(15, StatusTone.Positive, OkStatusId, PartialStatusId),
            ZoneFact(5, StatusTone.Positive, OkStatusId, PartialStatusId),
        };

        var view = new StatusResolver().ResolveZone(facts, Now);

        view.SourceStatusId.ShouldBe(OkStatusId);
        view.ObservationCount.ShouldBe(5);
    }

    [Fact]
    public void Zone_with_mixed_reports_is_partial_even_if_latest_is_positive()
    {
        var facts = new[]
        {
            ZoneFact(30, StatusTone.Negative, NoneStatusId, PartialStatusId),
            ZoneFact(20, StatusTone.Limited, WeakStatusId, PartialStatusId),
            ZoneFact(5, StatusTone.Positive, OkStatusId, PartialStatusId),
        };

        var view = new StatusResolver().ResolveZone(facts, Now);

        view.SourceStatusId.ShouldBe(PartialStatusId);
        view.ObservedAt.ShouldBe(Now.AddMinutes(-5));
    }

    [Fact]
    public void Zone_with_the_same_limited_status_keeps_it()
    {
        var facts = new[]
        {
            ZoneFact(30, StatusTone.Limited, WeakStatusId, PartialStatusId),
            ZoneFact(5, StatusTone.Limited, WeakStatusId, PartialStatusId),
        };

        new StatusResolver().ResolveZone(facts, Now).SourceStatusId.ShouldBe(WeakStatusId);
    }

    [Fact]
    public void Zone_ignores_reports_older_than_window_before_latest()
    {
        var facts = new[]
        {
            ZoneFact(150, StatusTone.Negative, NoneStatusId, PartialStatusId),
            ZoneFact(10, StatusTone.Positive, OkStatusId, PartialStatusId),
        };

        var view = new StatusResolver().ResolveZone(facts, Now);

        view.SourceStatusId.ShouldBe(OkStatusId);
        view.ObservationCount.ShouldBe(1);
    }

    [Fact]
    public void Zone_without_partial_status_falls_back_to_latest_limited_report()
    {
        var facts = new[]
        {
            ZoneFact(30, StatusTone.Limited, WeakStatusId),
            ZoneFact(20, StatusTone.Positive, OkStatusId),
            ZoneFact(5, StatusTone.Negative, NoneStatusId),
        };

        new StatusResolver().ResolveZone(facts, Now).SourceStatusId.ShouldBe(WeakStatusId);
    }

    private static ObservationFact Fact(
        DateTimeOffset observedAt,
        TimeSpan? sourceTimeout = null,
        SourceKind kind = SourceKind.Telegram,
        DateTimeOffset? receivedAt = null,
        bool isRetracted = false,
        DateTimeOffset? expiresAt = null,
        Guid? sourceId = null,
        Guid? sourceStatusId = null,
        Guid? id = null,
        StatusTone tone = StatusTone.Positive,
        Guid? partialStatusId = null)
    {
        var timeout = sourceTimeout ?? Timeout;

        return new ObservationFact(
            Id: id ?? Guid.NewGuid(),
            SourceId: sourceId ?? Guid.NewGuid(),
            SourceStatusId: sourceStatusId ?? Guid.NewGuid(),
            Kind: kind,
            ObservedAt: observedAt,
            ReceivedAt: receivedAt ?? observedAt,
            ExpiresAt: expiresAt ?? observedAt + timeout,
            SourceTimeout: timeout,
            IsRetracted: isRetracted,
            Tone: tone,
            PartialStatusId: partialStatusId);
    }
}
