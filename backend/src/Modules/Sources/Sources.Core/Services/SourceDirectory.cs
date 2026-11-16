using BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;

using Sources.Contracts;
using Sources.Core.Domain;

namespace Sources.Core.Services;

/// <summary>
/// Реализация <see cref="ISourceDirectory"/> для других модулей:
/// политика источника для расчёта <c>expires_at</c>, признак включённости и существование.
/// Прямая ссылка на <c>Sources.Core</c> из других модулей запрещена — читают только контракт.
/// </summary>
internal sealed class SourceDirectory(MayakDbContext db) : ISourceDirectory
{
    public async Task<SourcePolicyInfo?> GetPolicyAsync(Guid sourceId, CancellationToken ct)
    {
        var policy = await (
                from source in db.Set<Source>().AsNoTracking()
                join sourcePolicy in db.Set<SourcePolicy>().AsNoTracking()
                    on source.Id equals sourcePolicy.SourceId
                where source.Id == sourceId
                select new SourcePolicyInfo(
                    source.Id,
                    sourcePolicy.Timeout,
                    sourcePolicy.ConfidenceThreshold,
                    source.SourceType,
                    source.LinkByText))
            .FirstOrDefaultAsync(ct);

        return policy;
    }

    /// <summary>
    /// Статусы, которые может «докладывать» источник, в порядке показа (<c>sortOrder</c>, затем
    /// название). Пустой список — источника нет или у него нет статусов.
    /// </summary>
    public async Task<IReadOnlyList<SourceStatusInfo>> GetStatusesAsync(
        Guid sourceId,
        CancellationToken ct)
    {
        return await db.Set<SourceStatus>()
            .AsNoTracking()
            .Where(status => status.SourceId == sourceId)
            .OrderBy(status => status.SortOrder)
            .ThenBy(status => status.Name)
            .ThenBy(status => status.Id)
            .Select(status => new SourceStatusInfo(
                status.Id,
                status.Code,
                status.Name,
                status.Description,
                status.Color,
                status.Icon))
            .ToListAsync(ct);
    }

    public Task<bool> IsEnabledAsync(Guid sourceId, CancellationToken ct) =>
        db.Set<Source>()
            .AsNoTracking()
            .AnyAsync(source => source.Id == sourceId && source.IsEnabled, ct);

    public Task<bool> ExistsAsync(Guid sourceId, CancellationToken ct) =>
        db.Set<Source>()
            .AsNoTracking()
            .AnyAsync(source => source.Id == sourceId, ct);
}
