using System.Globalization;

using BuildingBlocks.Persistence;
using BuildingBlocks.Web;

using IdentityAccess.Contracts;

using Ingestion.Abstractions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

using Sources.Core.Domain;

namespace Sources.Core.Health;

/// <summary>
/// <c>GET /sources/{id}/health</c>: состояние источника, время последнего
/// сообщения и детали ошибки. Состояние <c>silent</c> не хранится в БД, а вычисляется при
/// чтении: включённый источник в состоянии <c>ok</c>, от которого дольше
/// <see cref="SilentAfter"/> не было сообщений, отдаётся как <c>silent</c>. У выключенного
/// источника <c>silent</c> не вычисляется — он и не должен получать сообщения.
/// </summary>
public static class SourceHealthEndpoint
{
    /// <summary>
    /// Порог «тишины» по умолчанию — 6 часов. Подходящего ключа в глобальных настройках нет
    /// (в реестре <c>GlobalSettingsRegistry</c> только лимит отметок, длина комментария, порог
    /// confidence, окно/порог аномалий, доля Fresh и глобальный прокси), поэтому контракт
    /// настроек не расширяется; при появлении ключа порог переедет в него без изменения ответа.
    /// </summary>
    internal static readonly TimeSpan SilentAfter = TimeSpan.FromHours(6);

    /// <summary>Регистрирует health-эндпоинт внутри группы <c>/sources</c>.</summary>
    public static RouteGroupBuilder MapSourceHealthEndpoint(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/{id:guid}/health", GetAsync)
            .RequireAuthorization(Permissions.SourceManage);

        return group;
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        MayakDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var source = await db.Set<Source>()
            .AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new SourceHealthRow(
                item.State,
                item.StateDetail,
                item.LastMessageAt,
                item.IsEnabled))
            .FirstOrDefaultAsync(ct);
        if (source is null)
        {
            return ProblemResponses.NotFound($"Источник {id} не существует");
        }

        var state = SourceStateExtensions.TryParseDatabaseValue(source.State, out var stored)
            ? stored
            : SourceState.Unknown;

        if (source.IsEnabled
            && state == SourceState.Ok
            && source.LastMessageAt is { } lastMessageAt
            && time.GetUtcNow() - lastMessageAt > SilentAfter)
        {
            state = SourceState.Silent;
        }

        var (floodWaitUntil, lastError) = ReadDetail(state, source.StateDetail);

        return Results.Ok(new SourceHealthResponse(
            state.ToDatabaseValue(),
            source.LastMessageAt,
            floodWaitUntil,
            lastError));
    }

    /// <summary>
    /// Раскладывает деталь из БД по полям контракта <c>SourceHealth</c>: для FloodWait это срок
    /// окончания ожидания (<c>floodWaitUntil</c>), для ошибок авторизации/адаптера — текст
    /// (<c>lastError</c>). Если деталь FloodWait не является ISO-8601-временем, она отдаётся
    /// как текст ошибки, чтобы не терять информацию.
    /// </summary>
    private static (DateTimeOffset? FloodWaitUntil, string? LastError) ReadDetail(
        SourceState state,
        string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return (null, null);
        }

        switch (state)
        {
            case SourceState.FloodWait when DateTimeOffset.TryParse(
                detail,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var until):
                return (until, null);
            case SourceState.FloodWait:
            case SourceState.AuthError:
            case SourceState.Error:
                return (null, detail);
            default:
                return (null, null);
        }
    }

    /// <summary>Минимальный набор полей источника, нужный health-эндпоинту.</summary>
    private sealed record SourceHealthRow(
        string State,
        string? StateDetail,
        DateTimeOffset? LastMessageAt,
        bool IsEnabled);
}

/// <summary>Ответ по схеме <c>SourceHealth</c> контракта.</summary>
/// <param name="State">Состояние в snake_case; вычисляемое значение — <c>silent</c>.</param>
/// <param name="LastMessageAt">Время последнего сообщения; <c>null</c> — сообщений не было.</param>
/// <param name="FloodWaitUntil">До какого времени Telegram просит подождать (FloodWait).</param>
/// <param name="LastError">Текст последней ошибки авторизации или адаптера.</param>
internal sealed record SourceHealthResponse(
    string State,
    DateTimeOffset? LastMessageAt,
    DateTimeOffset? FloodWaitUntil,
    string? LastError);
