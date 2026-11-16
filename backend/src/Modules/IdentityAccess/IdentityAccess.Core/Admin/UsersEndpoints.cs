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
/// <c>GET /admin/users</c> и <c>PUT /admin/users/{id}/roles</c>: список пользователей
/// с фильтрами по роли и имени и замена набора ролей целиком. Оба метода доступны только по
/// праву <c>users.roles.assign</c>. Изменение ролей и запись аудита сохраняются одним
/// <c>SaveChanges</c>.
/// </summary>
public static class UsersEndpoints
{
    private const int MaxSearchLength = 200;

    private const string RolesField = "roles";

    /// <summary>Регистрирует эндпоинты пользователей внутри текущего префикса (например, <c>/api/v1</c>).</summary>
    public static IEndpointRouteBuilder MapUsersEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints
            .MapGroup("/admin/users")
            .RequireAuthorization(Permissions.UsersRolesAssign);

        group.MapGet(string.Empty, ListAsync);
        group.MapPut("/{id:guid}/roles", SetRolesAsync);

        return endpoints;
    }

    /// <summary>
    /// Страница пользователей, отсортированная по <c>maxUserId</c> (уникален), с курсором на
    /// следующую страницу. Фильтр <c>role</c> отвечает в том числе на «список модераторов».
    /// </summary>
    private static async Task<IResult> ListAsync(
        string? role,
        string? q,
        int? limit,
        string? cursor,
        MayakDbContext db,
        CancellationToken ct)
    {
        if (!PageRequest.TryCreate(limit, cursor, out var paging, out var pagingFailure))
        {
            return pagingFailure;
        }

        Guid? roleId = null;
        if (!string.IsNullOrWhiteSpace(role))
        {
            if (!Roles.AllRoles.Contains(role, StringComparer.Ordinal))
            {
                return ProblemResponses.BadRequest($"Неизвестная роль: {role}");
            }

            roleId = await db.Set<AppRole>()
                .Where(candidate => candidate.Name == role)
                .Select(candidate => (Guid?)candidate.Id)
                .FirstOrDefaultAsync(ct);

            // Роль есть в матрице прав, но строки в БД нет: фильтр не должен «теряться».
            if (roleId is null)
            {
                return Results.Ok(new Page<AdminUserResponse>([], NextCursor: null));
            }
        }

        if (q is { Length: > MaxSearchLength })
        {
            return ProblemResponses.BadRequest($"Параметр q не должен быть длиннее {MaxSearchLength} символов");
        }

        long? afterMaxUserId = null;
        if (paging.CursorPayload is { } payload)
        {
            if (!long.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out var position))
            {
                return ProblemResponses.BadRequest("Некорректный курсор");
            }

            afterMaxUserId = position;
        }

        var users = db.Set<AppUser>().AsNoTracking();

        if (roleId is { } filterRoleId)
        {
            users = users.Where(user => db.Set<UserRole>()
                .Any(link => link.UserId == user.Id && link.RoleId == filterRoleId));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{EscapeLike(q.Trim())}%";
            users = users.Where(user => EF.Functions.ILike(user.DisplayName, pattern, "\\"));
        }

        if (afterMaxUserId is { } after)
        {
            users = users.Where(user => user.MaxUserId > after);
        }

        var page = await users
            .OrderBy(user => user.MaxUserId)
            .Take(paging.Limit + 1)
            .Select(user => new UserRow(
                user.Id,
                user.DisplayName,
                user.MaxUserId,
                user.CreatedAt,
                user.LastLoginAt))
            .ToListAsync(ct);

        string? nextCursor = null;
        if (page.Count > paging.Limit)
        {
            page.RemoveAt(page.Count - 1);
            nextCursor = PageRequest.EncodeCursor(page[^1].MaxUserId);
        }

        var userIds = page.Select(user => user.Id).ToArray();
        var roleNames = await (
                from link in db.Set<UserRole>()
                join roleEntity in db.Set<AppRole>() on link.RoleId equals roleEntity.Id
                where userIds.Contains(link.UserId)
                select new { link.UserId, roleEntity.Name })
            .ToListAsync(ct);

        var rolesByUser = roleNames
            .GroupBy(entry => entry.UserId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.Name).Order(StringComparer.Ordinal).ToArray());

        var items = page
            .Select(user => new AdminUserResponse(
                user.Id,
                user.DisplayName,
                user.MaxUserId,
                rolesByUser.TryGetValue(user.Id, out var names) ? names : [],
                user.CreatedAt,
                user.LastLoginAt))
            .ToArray();

        return Results.Ok(new Page<AdminUserResponse>(items, nextCursor));
    }

    /// <summary>
    /// Заменяет набор ролей целиком: недостающие привязки добавляются, лишние удаляются.
    /// Неизвестная роль — 422, несуществующий пользователь — 404. Действие пишется в аудит
    /// (<c>user.roles.assign</c>) и сохраняется вместе с изменением ролей.
    /// </summary>
    private static async Task<IResult> SetRolesAsync(
        Guid id,
        HttpContext context,
        MayakDbContext db,
        IAuditLog audit,
        CancellationToken ct)
    {
        using var document = await AdminRequestJson.TryReadObjectAsync(context.Request, ct);
        if (document is null)
        {
            return ProblemResponses.Validation(new ValidationError(RolesField, "Ожидается JSON-объект с полем roles"));
        }

        var errors = new List<ValidationError>();
        List<string>? requestedRoles = null;

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!string.Equals(property.Name, RolesField, StringComparison.Ordinal))
            {
                errors.Add(new ValidationError(property.Name, "Неизвестное поле"));
                continue;
            }

            requestedRoles = ParseRoles(property.Value, errors);
        }

        if (requestedRoles is null && errors.Count == 0)
        {
            errors.Add(new ValidationError(RolesField, "Поле roles обязательно"));
        }

        if (errors.Count > 0)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        var user = await db.Set<AppUser>().FirstOrDefaultAsync(candidate => candidate.Id == id, ct);
        if (user is null)
        {
            return ProblemResponses.NotFound($"Пользователь {id} не существует");
        }

        var roles = await db.Set<AppRole>()
            .Where(role => requestedRoles!.Contains(role.Name))
            .ToListAsync(ct);

        var links = await db.Set<UserRole>()
            .Where(link => link.UserId == id)
            .ToListAsync(ct);

        var currentRoleNames = await (
                from link in db.Set<UserRole>()
                join role in db.Set<AppRole>() on link.RoleId equals role.Id
                where link.UserId == id
                select role.Name)
            .ToListAsync(ct);

        var before = currentRoleNames.Order(StringComparer.Ordinal).ToArray();

        var targetRoleIds = roles.Select(role => role.Id).ToHashSet();
        var currentRoleIds = links.Select(link => link.RoleId).ToHashSet();

        foreach (var link in links.Where(link => !targetRoleIds.Contains(link.RoleId)))
        {
            db.Remove(link);
        }

        foreach (var roleId in targetRoleIds.Where(roleId => !currentRoleIds.Contains(roleId)))
        {
            db.Add(new UserRole { UserId = id, RoleId = roleId });
        }

        var after = requestedRoles!.Order(StringComparer.Ordinal).ToArray();

        // Один SaveChanges на изменение ролей и запись аудита: либо оба, либо ничего.
        await audit.WriteAsync(
            "user.roles.assign",
            "user",
            id.ToString(),
            new { roles = before },
            new { roles = after },
            ct: ct);

        await db.SaveChangesAsync(ct);

        return Results.Ok(new AdminUserResponse(
            user.Id,
            user.DisplayName,
            user.MaxUserId,
            after,
            user.CreatedAt,
            user.LastLoginAt));
    }

    /// <summary>Проверяет массив ролей по схеме <c>SetRolesRequest</c> (только известные роли, без дублей).</summary>
    private static List<string> ParseRoles(JsonElement json, List<ValidationError> errors)
    {
        var result = new List<string>();

        if (json.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new ValidationError(RolesField, "Ожидается массив ролей"));

            return result;
        }

        var index = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in json.EnumerateArray())
        {
            var field = $"{RolesField}[{index}]";
            index++;

            var name = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add(new ValidationError(field, "Ожидается название роли"));
                continue;
            }

            if (!Roles.AllRoles.Contains(name, StringComparer.Ordinal))
            {
                errors.Add(new ValidationError(field, $"Неизвестная роль: {name}"));
                continue;
            }

            if (!seen.Add(name))
            {
                errors.Add(new ValidationError(field, $"Роль указана более одного раза: {name}"));
                continue;
            }

            result.Add(name);
        }

        return result;
    }

    /// <summary>Экранирует спецсимволы LIKE, чтобы <c>q</c> искал подстроку буквально.</summary>
    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private sealed record UserRow(
        Guid Id,
        string DisplayName,
        long MaxUserId,
        DateTimeOffset CreatedAt,
        DateTimeOffset? LastLoginAt);

    /// <summary>Элемент ответа по схеме <c>AdminUser</c>.</summary>
    private sealed record AdminUserResponse(
        Guid Id,
        string DisplayName,
        long MaxUserId,
        IReadOnlyList<string> Roles,
        DateTimeOffset CreatedAt,
        DateTimeOffset? LastLoginAt);
}
