using Observations.Application;
using Observations.Contracts;
using Observations.Domain;

using Sources.Contracts;

using ContractSourceKind = Observations.Contracts.SourceKind;
using DomainSourceKind = Observations.Domain.SourceKind;

namespace Observations.Infrastructure;

/// <summary>
/// Реализация <see cref="IObservationFactory"/>:
/// переводит контракт модерации в доменное наблюдение, добавляет его и пересчитывает проекцию.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ModeratedObservation.ObservedAt"/> приходит из <c>raw_messages.posted_at</c>:
/// модератор подтверждает факт на момент сообщения, а не на момент решения. Поэтому
/// <c>allowExpired: true</c> — протухшее сообщение сохраняется для истории, но не влияет на
/// <c>current_statuses</c>.
/// </para>
/// <para>
/// Проекция пересчитывается в транзакции вызывающего: <c>CurrentStatusWriter</c> использует уже
/// открытую транзакцию <c>MayakDbContext</c>. <c>SaveChanges</c> здесь нужен, чтобы пересчёт
/// (он читает факты из таблицы) увидел только что добавленное наблюдение; коммит остаётся за
/// модерационным обработчиком, который одной транзакцией пишет кейс и аудит.
/// </para>
/// </remarks>
internal sealed class ObservationFactory(
    BuildingBlocks.Persistence.MayakDbContext db,
    IObservationRepository repository,
    IStatusRecalculator recalculator,
    ISourceDirectory sources,
    TimeProvider time) : IObservationFactory
{
    public async Task<Guid> CreateFromModerationAsync(
        ModeratedObservation m,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(m);

        var policy = await sources.GetPolicyAsync(m.SourceId, ct)
            ?? throw new DomainException($"Источник {m.SourceId} не существует.");

        var now = time.GetUtcNow();
        var observation = Observation.Create(
            new ObservationDraft(
                m.MapObjectId,
                m.SourceId,
                m.SourceStatusId,
                ToDomain(m.Kind),
                ObservedAt: m.ObservedAt,
                ReceivedAt: now,
                ReporterId: null,
                Confidence: m.Confidence,
                RawPayloadRef: m.RawMessageId.ToString(),
                Comment: null,
                Address: m.Address),
            policy.Timeout,
            now,
            allowExpired: true);

        await repository.AddAsync(observation, ct);

        // Факты для резолвера читаются SQL-ом из observations, поэтому наблюдение должно быть
        // сохранено до пересчёта. Транзакция та же: откат вызывающего уберёт и его.
        await db.SaveChangesAsync(ct);

        await recalculator.RecalculateAsync(observation.MapObjectId, now, ct);

        return observation.Id;
    }

    private static DomainSourceKind ToDomain(ContractSourceKind kind) => kind switch
    {
        ContractSourceKind.Manual => DomainSourceKind.Manual,
        ContractSourceKind.Moderator => DomainSourceKind.Moderator,
        ContractSourceKind.Telegram => DomainSourceKind.Telegram,
        ContractSourceKind.Max => DomainSourceKind.Max,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный тип источника."),
    };
}
