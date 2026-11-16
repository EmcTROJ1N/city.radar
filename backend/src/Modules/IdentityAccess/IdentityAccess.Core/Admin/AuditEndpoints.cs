using System.Globalization;
using System.Text.Json;

using BuildingBlocks.Persistence;
using BuildingBlocks.Web;

using IdentityAccess.Contracts;
using IdentityAccess.Core.Domain;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace IdentityAccess.Core.Admin;

/// <summary>
/// <c>GET /admin/audit</c>: журнал аудита только для чтения, новые записи сверху,
/// фильтры по актору, действию, объекту и периоду. Доступ — только по праву <c>audit.read</c>.
/// </summary>
public static class AuditEndpoints
{
    /// <summary>Регистрирует журнал аудита внутри текущего префикса (например, <c>/api/v1</c>).</summary>
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet("/admin/audit", ListAsync)
            .RequireAuthorization(Permissions.AuditRead);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        Guid? actorId,
        string? action,
        string? entityType,
        string? entityId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? limit,
        string? cursor,
        MayakDbContext db,
        CancellationToken ct)
    {
        if (!PageRequest.TryCreate(limit, cursor, out var paging, out var pagingFailure))
        {
            return pagingFailure;
        }

        (DateTimeOffset At, long Id)? after = null;
        if (paging.CursorPayload is { } payload)
        {
            if (!TryParseCursor(payload, out var at, out var id))
            {
                return ProblemResponses.BadRequest("Некорректный курсор");
            }

            after = (at, id);
        }

        var entries = db.Set<AuditEntry>().AsNoTracking();

        if (actorId is { } actor)
        {
            entries = entries.Where(entry => entry.ActorId == actor);
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            entries = entries.Where(entry => entry.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(entityType))
        {
            entries = entries.Where(entry => entry.EntityType == entityType);
        }

        if (!string.IsNullOrWhiteSpace(entityId))
        {
            entries = entries.Where(entry => entry.EntityId == entityId);
        }

        if (from is { } fromValue)
        {
            entries = entries.Where(entry => entry.At >= fromValue);
        }

        if (to is { } toValue)
        {
            entries = entries.Where(entry => entry.At <= toValue);
        }

        if (after is { } position)
        {
            entries = entries.Where(entry => entry.At < position.At
                || (entry.At == position.At && entry.Id < position.Id));
        }

        var page = await entries
            .OrderByDescending(entry => entry.At)
            .ThenByDescending(entry => entry.Id)
            .Take(paging.Limit + 1)
            .ToListAsync(ct);

        string? nextCursor = null;
        if (page.Count > paging.Limit)
        {
            page.RemoveAt(page.Count - 1);
            nextCursor = PageRequest.EncodeCursor(page[^1].At.UtcTicks, page[^1].Id);
        }

        var actorIds = page
            .Where(entry => entry.ActorId is not null)
            .Select(entry => entry.ActorId!.Value)
            .Distinct()
            .ToArray();

        var actorNames = actorIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await db.Set<AppUser>()
                .AsNoTracking()
                .Where(user => actorIds.Contains(user.Id))
                .ToDictionaryAsync(user => user.Id, user => user.DisplayName, ct);

        var items = page
            .Select(entry => new AuditEntryResponse(
                entry.Id,
                entry.ActorId,
                entry.ActorId is { } entryActorId && actorNames.TryGetValue(entryActorId, out var name)
                    ? name
                    : null,
                entry.Action,
                entry.EntityType,
                entry.EntityId,
                ParseJson(entry.Before),
                ParseJson(entry.After),
                entry.Reason,
                entry.Ip?.ToString(),
                entry.UserAgent,
                entry.At))
            .ToArray();

        return Results.Ok(new Page<AuditEntryResponse>(items, nextCursor));
    }

    private static bool TryParseCursor(string payload, out DateTimeOffset at, out long id)
    {
        at = default;
        id = 0;

        var parts = payload.Split(':', 2);
        if (parts.Length != 2)
        {
            return false;
        }

        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
        {
            return false;
        }

        at = new DateTimeOffset(ticks, TimeSpan.Zero);

        return true;
    }

    /// <summary>jsonb из БД отдаётся в ответе объектом, а не строкой.</summary>
    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Элемент ответа по схеме <c>AuditEntry</c>.</summary>
    private sealed record AuditEntryResponse(
        long Id,
        Guid? ActorId,
        string? ActorName,
        string Action,
        string? EntityType,
        string? EntityId,
        JsonElement? Before,
        JsonElement? After,
        string? Reason,
        string? Ip,
        string? UserAgent,
        DateTimeOffset At);

}
