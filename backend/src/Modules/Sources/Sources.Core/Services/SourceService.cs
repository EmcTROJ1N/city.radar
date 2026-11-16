using System.Globalization;
using System.Text.Json;

using BuildingBlocks.Persistence;
using BuildingBlocks.Web;

using IdentityAccess.Contracts;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

using Npgsql;

using Sources.Contracts;
using Sources.Core.Domain;
using Sources.Core.Endpoints;

namespace Sources.Core.Services;

/// <summary>
/// Источники данных: CRUD, политики,
/// включение/выключение и аудит. Источник и его политика создаются и изменяются одним
/// <c>SaveChanges</c> вместе с записью аудита — в одной транзакции.
/// </summary>
internal sealed class SourceService(
    MayakDbContext db,
    IAuditLog audit,
    ICurrentUser currentUser,
    ISourceDeletionGuard deletionGuard)
{
    private const string EntityType = "source";
    private const string SourceTypeField = "sourceType";
    private const string TelegramSourceType = "telegram_chat";
    private const string TelegramBotMode = "bot";

    /// <summary>Курсорная страница источников с фильтрами контракта.</summary>
    public async Task<IResult> ListAsync(
        SourceFilter filter,
        PageRequest page,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);

        if (!TryReadOffset(page, out var offset))
        {
            return ProblemResponses.BadRequest("Некорректный курсор");
        }

        var query = db.Set<Source>().AsNoTracking();

        if (filter.SourceType is { } sourceType)
        {
            query = query.Where(source => source.SourceType == sourceType);
        }

        if (filter.IsEnabled is { } isEnabled)
        {
            query = query.Where(source => source.IsEnabled == isEnabled);
        }

        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            // pg_trgm для источников не заявлен, но ILIKE по названию — поведение контракта.
            var pattern = $"%{EscapeLikePattern(filter.Query.Trim())}%";
            query = query.Where(source => EF.Functions.ILike(source.Name, pattern, "\\"));
        }

        var rows = await query
            .OrderBy(source => source.Name)
            .ThenBy(source => source.Id)
            .Skip(offset)
            .Take(page.Limit + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > page.Limit;
        var items = hasMore ? rows.Take(page.Limit).ToList() : rows;
        var sourceIds = items.Select(item => item.Id).ToArray();
        var policies = await ReadPoliciesAsync(sourceIds, ct);
        var statuses = await ReadStatusesAsync(sourceIds, ct);

        var responses = items
            .Select(item => ToResponse(
                item,
                policies.GetValueOrDefault(item.Id),
                statuses.GetValueOrDefault(item.Id) ?? []))
            .ToArray();

        var nextCursor = hasMore
            ? PageRequest.EncodeCursor(offset + items.Count)
            : null;

        return Results.Ok(new Page<SourceResponse>(responses, nextCursor));
    }

    /// <summary>Источник вместе с политикой и статусами.</summary>
    public async Task<IResult> GetAsync(Guid id, CancellationToken ct)
    {
        var source = await db.Set<Source>()
            .AsNoTracking()
            .Include(item => item.Policy)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (source is null)
        {
            return ProblemResponses.NotFound($"Источник {id} не существует");
        }

        var statuses = await ReadStatusesAsync([source.Id], ct);

        return Results.Ok(ToResponse(
            source,
            source.Policy,
            statuses.GetValueOrDefault(source.Id) ?? []));
    }

    /// <summary>
    /// Создание источника. Источник создаётся включённым (значение по умолчанию в БД), политика
    /// пишется тем же <c>SaveChanges</c>; статусов у нового источника ещё нет.
    /// </summary>
    public async Task<IResult> CreateAsync(SourceInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!await SourceTypeExistsAsync(input.SourceType, ct))
        {
            return ProblemResponses.Validation(
                new ValidationError(SourceTypeField, $"Неизвестный тип источника: {input.SourceType}"));
        }

        var source = new Source
        {
            Id = Guid.NewGuid(),
            Name = input.Name,
            SourceType = input.SourceType,
            Settings = WriteSettings(input.SourceType, input.ChatId),
            UserPrompt = input.UserPrompt,
            LinkByText = input.LinkByText,
            IsEnabled = true,
            State = "unknown",
            CreatedBy = currentUser.UserId,
            Policy = new SourcePolicy
            {
                Timeout = TimeSpan.FromSeconds(input.TimeoutSeconds),
                ConfidenceThreshold = input.ConfidenceThreshold,
                Prefilter = input.Prefilter,
            },
        };

        source.Policy.SourceId = source.Id;
        db.Add(source);

        await audit.WriteAsync(
            "source.create",
            EntityType,
            source.Id.ToString(),
            null,
            ToAuditSnapshot(source),
            ct: ct);

        var conflict = await SaveAsync(ct);
        if (conflict is not null)
        {
            return conflict;
        }

        return Results.Created($"/api/v1/sources/{source.Id}", ToResponse(source, source.Policy, []));
    }

    /// <summary>
    /// Изменение настроек источника и его политики. В аудит попадают только изменённые поля;
    /// для <c>userPrompt</c> — старое и новое значение.
    /// </summary>
    public async Task<IResult> UpdateAsync(Guid id, SourceInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var source = await db.Set<Source>()
            .Include(item => item.Policy)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (source is null)
        {
            return ProblemResponses.NotFound($"Источник {id} не существует");
        }

        if (!await SourceTypeExistsAsync(input.SourceType, ct))
        {
            return ProblemResponses.Validation(
                new ValidationError(SourceTypeField, $"Неизвестный тип источника: {input.SourceType}"));
        }

        var oldChatId = ReadChatId(source.Settings);
        var oldMode = ReadMode(source.Settings);
        var policy = source.Policy;
        if (policy is null)
        {
            // Политика обязательна по схеме; строка могла быть удалена только вне API.
            policy = new SourcePolicy
            {
                SourceId = source.Id,
                Timeout = TimeSpan.FromSeconds(input.TimeoutSeconds),
            };
            source.Policy = policy;
            db.Add(policy);
        }

        var timeoutSeconds = (int)policy.Timeout.TotalSeconds;
        var before = new Dictionary<string, object?>(StringComparer.Ordinal);
        var after = new Dictionary<string, object?>(StringComparer.Ordinal);

        Track(before, after, "name", source.Name, input.Name);
        Track(before, after, "sourceType", source.SourceType, input.SourceType);
        Track(before, after, "chatId", oldChatId, input.ChatId);
        Track(before, after, "mode", oldMode, ModeFor(input.SourceType));
        Track(before, after, "timeoutSeconds", timeoutSeconds, input.TimeoutSeconds);
        Track(before, after, "confidenceThreshold", policy.ConfidenceThreshold, input.ConfidenceThreshold);
        Track(before, after, "prefilter", policy.Prefilter, input.Prefilter);
        Track(before, after, "userPrompt", source.UserPrompt, input.UserPrompt);
        Track(before, after, "linkByText", source.LinkByText, input.LinkByText);

        source.Name = input.Name;
        source.SourceType = input.SourceType;
        source.Settings = WriteSettings(input.SourceType, input.ChatId);
        source.UserPrompt = input.UserPrompt;
        source.LinkByText = input.LinkByText;
        policy.Timeout = TimeSpan.FromSeconds(input.TimeoutSeconds);
        policy.ConfidenceThreshold = input.ConfidenceThreshold;
        policy.Prefilter = input.Prefilter;

        if (before.Count > 0)
        {
            await audit.WriteAsync("source.update", EntityType, source.Id.ToString(), before, after, ct: ct);
        }

        var conflict = await SaveAsync(ct);
        if (conflict is not null)
        {
            return conflict;
        }

        var statuses = await ReadStatusesAsync([source.Id], ct);

        return Results.Ok(ToResponse(source, policy, statuses.GetValueOrDefault(source.Id) ?? []));
    }

    /// <summary>
    /// Удаление источника. Источник с наблюдениями удалять нельзя (409): его отключают через
    /// <c>/sources/{id}/disable</c>. Привязки <c>map_object_sources</c> снимаются каскадом FK.
    /// </summary>
    public async Task<IResult> DeleteAsync(Guid id, CancellationToken ct)
    {
        var source = await db.Set<Source>()
            .Include(item => item.Policy)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (source is null)
        {
            return ProblemResponses.NotFound($"Источник {id} не существует");
        }

        if (await deletionGuard.HasObservationsAsync(id, ct))
        {
            return ProblemResponses.Conflict(
                "У источника есть наблюдения, удалить его нельзя: отключите источник "
                + $"через POST /api/v1/sources/{id}/disable");
        }

        var before = ToAuditSnapshot(source);
        db.Remove(source);

        await audit.WriteAsync(
            "source.delete",
            EntityType,
            source.Id.ToString(),
            before,
            null,
            ct: ct);

        var conflict = await SaveAsync(ct);
        if (conflict is not null)
        {
            return conflict;
        }

        return Results.NoContent();
    }

    /// <summary>Включение источника; изменение и запись аудита — одним <c>SaveChanges</c>.</summary>
    public Task<IResult> EnableAsync(Guid id, CancellationToken ct) =>
        SetEnabledAsync(id, enabled: true, "source.enable", ct);

    /// <summary>Выключение источника без перезапуска Worker.</summary>
    public Task<IResult> DisableAsync(Guid id, CancellationToken ct) =>
        SetEnabledAsync(id, enabled: false, "source.disable", ct);

    private async Task<IResult> SetEnabledAsync(
        Guid id,
        bool enabled,
        string action,
        CancellationToken ct)
    {
        var source = await db.Set<Source>()
            .Include(item => item.Policy)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (source is null)
        {
            return ProblemResponses.NotFound($"Источник {id} не существует");
        }

        var before = new { isEnabled = source.IsEnabled };
        source.IsEnabled = enabled;

        await audit.WriteAsync(
            action,
            EntityType,
            source.Id.ToString(),
            before,
            new { isEnabled = source.IsEnabled },
            ct: ct);

        var conflict = await SaveAsync(ct);
        if (conflict is not null)
        {
            return conflict;
        }

        var statuses = await ReadStatusesAsync([source.Id], ct);

        return Results.Ok(ToResponse(
            source,
            source.Policy,
            statuses.GetValueOrDefault(source.Id) ?? []));
    }

    private Task<bool> SourceTypeExistsAsync(string code, CancellationToken ct) =>
        db.Set<SourceType>()
            .AsNoTracking()
            .AnyAsync(sourceType => sourceType.Code == code && sourceType.IsEnabled, ct);

    private async Task<Dictionary<Guid, SourcePolicy>> ReadPoliciesAsync(
        Guid[] sourceIds,
        CancellationToken ct)
    {
        if (sourceIds.Length == 0)
        {
            return [];
        }

        var policies = await db.Set<SourcePolicy>()
            .AsNoTracking()
            .Where(policy => sourceIds.Contains(policy.SourceId))
            .ToListAsync(ct);

        return policies.ToDictionary(policy => policy.SourceId);
    }

    /// <summary>
    /// Статусы страницы источников одним запросом (<c>WHERE source_id IN (...)</c>), чтобы
    /// список не порождал N+1. Порядок показа — <c>sortOrder</c>, затем название, затем id.
    /// </summary>
    private async Task<Dictionary<Guid, IReadOnlyList<SourceStatusResponse>>> ReadStatusesAsync(
        Guid[] sourceIds,
        CancellationToken ct)
    {
        if (sourceIds.Length == 0)
        {
            return [];
        }

        var statuses = await db.Set<SourceStatus>()
            .AsNoTracking()
            .Where(status => sourceIds.Contains(status.SourceId))
            .OrderBy(status => status.SourceId)
            .ThenBy(status => status.SortOrder)
            .ThenBy(status => status.Name)
            .ThenBy(status => status.Id)
            .ToListAsync(ct);

        return statuses
            .GroupBy(status => status.SourceId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<SourceStatusResponse>)
                    [.. group.Select(SourceStatusService.ToResponse)]);
    }

    /// <summary>
    /// Сохраняет бизнес-изменение и запись аудита одним <c>SaveChanges</c>. Нарушение FK
    /// (например, тип источника удалён параллельно) превращается в 409, а не в 500.
    /// </summary>
    private async Task<IResult?> SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);

            return null;
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            return ProblemResponses.Conflict("Источник ссылается на несуществующую запись");
        }
    }

    private static void Track(
        Dictionary<string, object?> before,
        Dictionary<string, object?> after,
        string field,
        object? oldValue,
        object? newValue)
    {
        if (Equals(oldValue, newValue))
        {
            return;
        }

        before[field] = oldValue;
        after[field] = newValue;
    }

    /// <summary>
    /// Настройки источника в <c>sources.settings</c>. Для Telegram в первой версии жёстко
    /// выставляется режим Bot API: UI умеет только токен бота, а юзербот скрыт.
    /// </summary>
    private static string WriteSettings(string sourceType, string chatId)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["chatId"] = chatId,
        };

        if (string.Equals(sourceType, TelegramSourceType, StringComparison.Ordinal))
        {
            settings["mode"] = TelegramBotMode;
        }

        return JsonSerializer.Serialize(settings);
    }

    /// <summary>Режим чтения Telegram из <c>settings</c>; <c>null</c>, если режим не задан.</summary>
    private static string? ReadMode(string settings)
    {
        try
        {
            using var document = JsonDocument.Parse(settings);

            return document.RootElement.TryGetProperty("mode", out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Режим, который будет записан в <c>settings</c> для данного типа источника.</summary>
    private static string? ModeFor(string sourceType) =>
        string.Equals(sourceType, TelegramSourceType, StringComparison.Ordinal)
            ? TelegramBotMode
            : null;

    /// <summary>Идентификатор чата из <c>settings</c>; пустая строка, если поле не задано.</summary>
    private static string ReadChatId(string settings)
    {
        try
        {
            using var document = JsonDocument.Parse(settings);

            return document.RootElement.TryGetProperty("chatId", out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static SourceResponse ToResponse(
        Source source,
        SourcePolicy? policy,
        IReadOnlyList<SourceStatusResponse> statuses) =>
        new(
            source.Id,
            source.Name,
            source.SourceType,
            ReadChatId(source.Settings),
            policy is null ? 0 : (int)policy.Timeout.TotalSeconds,
            policy?.ConfidenceThreshold,
            ParseJson(policy?.Prefilter),
            source.UserPrompt,
            source.LinkByText,
            source.IsEnabled,
            source.State,
            source.LastMessageAt,
            source.CreatedAt,
            statuses);

    private static SourceAuditSnapshot ToAuditSnapshot(Source source) =>
        new(
            source.Name,
            source.SourceType,
            ReadChatId(source.Settings),
            source.Policy is null ? 0 : (int)source.Policy.Timeout.TotalSeconds,
            source.Policy?.ConfidenceThreshold,
            ParseJson(source.Policy?.Prefilter),
            source.UserPrompt,
            source.LinkByText,
            source.IsEnabled,
            source.State);

    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }

    /// <summary>Курсор списка источников — непрозрачная позиция страницы (смещение).</summary>
    private static bool TryReadOffset(PageRequest page, out int offset)
    {
        offset = 0;

        if (!page.HasCursor)
        {
            return true;
        }

        return int.TryParse(
                page.CursorPayload,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out offset)
            && offset >= 0;
    }

    /// <summary>Экранирует <c>%</c>, <c>_</c> и <c>\</c>, чтобы они искались буквально.</summary>
    private static string EscapeLikePattern(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}

/// <summary>Фильтры списка источников (<c>GET /sources</c>).</summary>
internal sealed record SourceFilter(string? Query, string? SourceType, bool? IsEnabled);

/// <summary>Элемент ответа по схеме <c>Source</c>; статусы — по схеме <c>SourceStatus</c>.</summary>
internal sealed record SourceResponse(
    Guid Id,
    string Name,
    string SourceType,
    string ChatId,
    int TimeoutSeconds,
    float? ConfidenceThreshold,
    JsonElement? Prefilter,
    string UserPrompt,
    bool LinkByText,
    bool IsEnabled,
    string State,
    DateTimeOffset? LastMessageAt,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SourceStatusResponse> Statuses);

/// <summary>Снимок источника для <c>audit_entries.before/after</c>.</summary>
internal sealed record SourceAuditSnapshot(
    string Name,
    string SourceType,
    string ChatId,
    int TimeoutSeconds,
    float? ConfidenceThreshold,
    JsonElement? Prefilter,
    string UserPrompt,
    bool LinkByText,
    bool IsEnabled,
    string State);
