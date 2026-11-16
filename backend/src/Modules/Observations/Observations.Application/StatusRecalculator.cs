using IdentityAccess.Contracts;

using Observations.Domain;

namespace Observations.Application;

/// <summary>
/// Оркестрация пересчёта проекции: факты из таблицы <c>observations</c>
/// → <see cref="StatusResolver"/> с долей Fresh из глобальных настроек → запись строки
/// <c>current_statuses</c>.
/// </summary>
/// <remarks>
/// Последовательность задаёт <see cref="ICurrentStatusWriter"/>: блокировка по фиксации берётся
/// до чтения фактов, поэтому два одновременных пересчёта не могут записать в проекцию наблюдение,
/// которое уже не самое свежее (Review Focus 2). Настройки читаются до блокировки — это снимок
/// только для расчёта свежести и он не влияет на выбор победителя.
/// </remarks>
public sealed class StatusRecalculator(
    IObservationRepository repository,
    ICurrentStatusWriter writer,
    IGlobalSettings settings) : IStatusRecalculator
{
    public async Task RecalculateAsync(Guid mapObjectId, DateTimeOffset now, CancellationToken ct)
    {
        var snapshot = await settings.GetAsync(ct);
        var resolver = new StatusResolver(snapshot.FreshnessFreshRatio);

        await writer.WriteAsync(
            mapObjectId,
            now,
            async token => resolver.Resolve(await repository.GetFactsAsync(mapObjectId, token), now),
            ct);
    }
}
