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
/// Статусы источника: CRUD внутри источника, создание из записи
/// справочника или собственный статус, аудит. Статусы принадлежат источнику, поэтому
/// уникальность кода проверяется в пределах <c>(source_id, code)</c>; изменение и запись
/// аудита сохраняются одним <c>SaveChanges</c>.
/// </summary>
internal sealed class SourceStatusService(
    MayakDbContext db,
    IAuditLog audit,
    ISourceStatusDeletionGuard deletionGuard)
{
    private const string EntityType = "source_status";
    private const string DefinitionField = "statusDefinitionId";

    /// <summary>Статусы источника в порядке показа: сначала <c>sortOrder</c>, затем название.</summary>
    public async Task<IResult> ListAsync(Guid sourceId, CancellationToken ct)
    {
        if (!await SourceExistsAsync(sourceId, ct))
        {
            return SourceNotFound(sourceId);
        }

        var statuses = await db.Set<SourceStatus>()
            .AsNoTracking()
            .Where(status => status.SourceId == sourceId)
            .OrderBy(status => status.SortOrder)
            .ThenBy(status => status.Name)
            .ThenBy(status => status.Id)
            .ToListAsync(ct);

        return Results.Ok(statuses.Select(ToResponse).ToArray());
    }

    /// <summary>
    /// Добавление статуса источнику. Если задан <c>statusDefinitionId</c>, <c>code</c>,
    /// <c>name</c>, <c>color</c> и <c>icon</c> копируются из справочника,
    /// описание для LLM задаётся отдельно и по умолчанию пустое.
    /// </summary>
    public async Task<IResult> CreateAsync(Guid sourceId, SourceStatusInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!await SourceExistsAsync(sourceId, ct))
        {
            return SourceNotFound(sourceId);
        }

        StatusDefinitionLookup? definition = null;
        if (input.StatusDefinitionId is { } definitionId)
        {
            definition = await FindDefinitionAsync(definitionId, ct);
            if (definition is null)
            {
                return ProblemResponses.Validation(
                    new ValidationError(DefinitionField, $"Запись справочника {definitionId} не существует"));
            }
        }

        var code = definition?.Code ?? input.Code!;
        var duplicate = await db.Set<SourceStatus>()
            .AsNoTracking()
            .AnyAsync(status => status.SourceId == sourceId && status.Code == code, ct);
        if (duplicate)
        {
            return Duplicate(code);
        }

        var status = new SourceStatus
        {
            Id = Guid.NewGuid(),
            SourceId = sourceId,
            StatusDefinitionId = input.StatusDefinitionId,
            Code = code,
            Name = definition?.Name ?? input.Name!,
            Description = input.Description ?? string.Empty,
            Color = definition?.Color ?? input.Color!,
            Icon = definition?.Icon ?? input.Icon!,
            SortOrder = input.SortOrder,
        };

        db.Add(status);

        await audit.WriteAsync(
            "source.status.create",
            EntityType,
            status.Id.ToString(),
            null,
            ToResponse(status),
            ct: ct);

        var conflict = await SaveAsync(code, ct);
        if (conflict is not null)
        {
            return conflict;
        }

        return Results.Created(
            $"/api/v1/sources/{sourceId}/statuses/{status.Id}",
            ToResponse(status));
    }

    /// <summary>Изменение статуса: код, название, описание для LLM, цвет, иконка и порядок.</summary>
    public async Task<IResult> UpdateAsync(
        Guid sourceId,
        Guid statusId,
        SourceStatusInput input,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var status = await db.Set<SourceStatus>()
            .FirstOrDefaultAsync(item => item.SourceId == sourceId && item.Id == statusId, ct);
        if (status is null)
        {
            return StatusNotFound(sourceId, statusId);
        }

        if (input.StatusDefinitionSpecified && input.StatusDefinitionId is { } definitionId
            && await FindDefinitionAsync(definitionId, ct) is null)
        {
            return ProblemResponses.Validation(
                new ValidationError(DefinitionField, $"Запись справочника {definitionId} не существует"));
        }

        var duplicate = await db.Set<SourceStatus>()
            .AsNoTracking()
            .AnyAsync(
                item => item.SourceId == sourceId && item.Code == input.Code && item.Id != statusId,
                ct);
        if (duplicate)
        {
            return Duplicate(input.Code!);
        }

        var before = ToResponse(status);

        status.Code = input.Code!;
        status.Name = input.Name!;
        status.Description = input.Description ?? string.Empty;
        status.Color = input.Color!;
        status.Icon = input.Icon!;
        status.SortOrder = input.SortOrder;

        if (input.StatusDefinitionSpecified)
        {
            status.StatusDefinitionId = input.StatusDefinitionId;
        }

        await audit.WriteAsync(
            "source.status.update",
            EntityType,
            status.Id.ToString(),
            before,
            ToResponse(status),
            ct: ct);

        var conflict = await SaveAsync(status.Code, ct);
        if (conflict is not null)
        {
            return conflict;
        }

        return Results.Ok(ToResponse(status));
    }

    /// <summary>
    /// Удаление статуса. Статус, по которому есть наблюдения, удалять нельзя (409):
    /// проверка скрыта за <see cref="ISourceStatusDeletionGuard"/> (реальная реализация —).
    /// </summary>
    public async Task<IResult> DeleteAsync(Guid sourceId, Guid statusId, CancellationToken ct)
    {
        var status = await db.Set<SourceStatus>()
            .FirstOrDefaultAsync(item => item.SourceId == sourceId && item.Id == statusId, ct);
        if (status is null)
        {
            return StatusNotFound(sourceId, statusId);
        }

        if (await deletionGuard.HasObservationsAsync(sourceId, statusId, ct))
        {
            return ProblemResponses.Conflict(
                "По статусу есть наблюдения, удалить его нельзя: отключите источник "
                + "или оставьте статус в истории");
        }

        var before = ToResponse(status);
        db.Remove(status);

        await audit.WriteAsync(
            "source.status.delete",
            EntityType,
            status.Id.ToString(),
            before,
            null,
            ct: ct);

        var conflict = await SaveAsync(status.Code, ct);
        if (conflict is not null)
        {
            return conflict;
        }

        return Results.NoContent();
    }

    private Task<bool> SourceExistsAsync(Guid sourceId, CancellationToken ct) =>
        db.Set<Source>().AsNoTracking().AnyAsync(source => source.Id == sourceId, ct);

    /// <summary>
    /// Запись библиотеки стилей <c>status_definitions</c> (модуль каталога) читается запросом
    /// по таблице: прямая ссылка на <c>Catalog.Core</c> из другого модуля запрещена.
    /// Для несопоставленного типа EF применяет snake_case-имена свойств, поэтому колонки
    /// выбираются без изменения регистра.
    /// </summary>
    private Task<StatusDefinitionLookup?> FindDefinitionAsync(Guid definitionId, CancellationToken ct) =>
        db.Database
            .SqlQueryRaw<StatusDefinitionLookup>(
                "select id, code, name, color, icon from status_definitions where id = {0}",
                definitionId)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Сохраняет изменение и запись аудита одним <c>SaveChanges</c>. Нарушение уникальности
    /// <c>(source_id, code)</c> (23505) превращается в 409 и при гонке двух запросов.
    /// </summary>
    private async Task<IResult?> SaveAsync(string code, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);

            return null;
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Duplicate(code);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            return ProblemResponses.Conflict("Статус ссылается на несуществующую запись");
        }
    }

    private static IResult Duplicate(string code) =>
        ProblemResponses.Conflict($"Статус с кодом '{code}' уже существует у этого источника");

    private static IResult SourceNotFound(Guid sourceId) =>
        ProblemResponses.NotFound($"Источник {sourceId} не существует");

    private static IResult StatusNotFound(Guid sourceId, Guid statusId) =>
        ProblemResponses.NotFound($"Статус {statusId} источника {sourceId} не существует");

    internal static SourceStatusResponse ToResponse(SourceStatus status) =>
        new(
            status.Id,
            status.Code,
            status.Name,
            status.Description,
            status.Color,
            status.Icon,
            status.SortOrder,
            status.StatusDefinitionId);

    /// <summary>Проекция записи справочника, нужная для копирования полей в статус источника.</summary>
    private sealed class StatusDefinitionLookup
    {
        public Guid Id { get; set; }

        public string Code { get; set; } = null!;

        public string Name { get; set; } = null!;

        public string Color { get; set; } = null!;

        public string Icon { get; set; } = null!;
    }
}

/// <summary>Элемент ответа по схеме <c>SourceStatus</c>.</summary>
internal sealed record SourceStatusResponse(
    Guid Id,
    string Code,
    string Name,
    string Description,
    string Color,
    string Icon,
    int SortOrder,
    Guid? StatusDefinitionId);
