using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;

using BuildingBlocks.Persistence;
using BuildingBlocks.Web;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Moderation.Core.Domain;

using Npgsql;
using NpgsqlTypes;

namespace Moderation.Core.Services;

/// <summary>
/// Чтение очереди модерации: список кейсов с фильтрами
/// <c>kind</c>/<c>state</c> и карточка кейса с исходным сообщением и <c>evidenceQuote</c>.
/// </summary>
/// <remarks>
/// Данные других модулей (названия источников и фиксаций, <c>raw_messages</c>) читаются
/// read-only SQL-проекциями, как предписывает решение по БД: чужие сущности в модель
/// модерации не попадают. Автор сообщения (<c>raw_messages.author</c>) не выбирается и наружу
/// не отдаётся.
/// </remarks>
internal sealed class ModerationCaseService(MayakDbContext db)
{
    /// <summary>Длина выдержки сообщения в списке/карточке (<c>messageExcerpt</c>).</summary>
    private const int ExcerptLength = 200;

    /// <summary>
    /// Очередь: по умолчанию только открытые кейсы, сортировка по <c>created_at</c> — старые
    /// сверху. Курсор — смещение, как в остальных списках контракта.
    /// </summary>
    public async Task<IResult> ListAsync(
        string? kind,
        string? state,
        PageRequest page,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        var errors = new List<ValidationError>();
        var kindValue = kind;
        if (kindValue is not null && !ModerationCaseKinds.IsKnown(kindValue))
        {
            errors.Add(new ValidationError("kind", $"Неизвестный вид кейса: {kindValue}"));
        }

        var stateValue = state ?? ModerationCaseStates.Open;
        if (!ModerationCaseStates.IsKnown(stateValue))
        {
            errors.Add(new ValidationError("state", $"Неизвестное состояние кейса: {stateValue}"));
        }

        if (errors.Count > 0)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        if (!TryReadOffset(page, out var offset))
        {
            return ProblemResponses.BadRequest("Некорректный курсор");
        }

        var query = db.Set<ModerationCase>()
            .AsNoTracking()
            .Where(moderationCase => moderationCase.State == stateValue);
        if (kindValue is not null)
        {
            query = query.Where(moderationCase => moderationCase.Kind == kindValue);
        }

        var rows = await query
            .OrderBy(moderationCase => moderationCase.CreatedAt)
            .ThenBy(moderationCase => moderationCase.Id)
            .Skip(offset)
            .Take(page.Limit + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > page.Limit;
        var items = hasMore ? rows.Take(page.Limit).ToList() : rows;

        var sourceNames = await ReadNamesAsync(
            "select id, name from sources where id = any(@ids)",
            [.. items.Where(item => item.SourceId is not null).Select(item => item.SourceId!.Value)],
            ct);
        var objectNames = await ReadNamesAsync(
            "select id, name from map_objects where id = any(@ids)",
            [.. items.Where(item => item.MapObjectId is not null).Select(item => item.MapObjectId!.Value)],
            ct);
        var excerpts = await ReadMessageExcerptsAsync(
            [.. items.Where(item => item.RawMessageId is not null).Select(item => item.RawMessageId!.Value)],
            ct);

        var responses = items
            .Select(item => new ModerationCaseSummary(
                item.Id,
                item.Kind,
                item.State,
                item.CreatedAt,
                item.SourceId is { } sourceId ? sourceNames.GetValueOrDefault(sourceId) : null,
                item.MapObjectId is { } objectId ? objectNames.GetValueOrDefault(objectId) : null,
                item.RawMessageId is { } rawId ? excerpts.GetValueOrDefault(rawId) : null))
            .ToArray();

        var nextCursor = hasMore
            ? PageRequest.EncodeCursor(offset + items.Count)
            : null;

        return Results.Ok(new Page<ModerationCaseSummary>(responses, nextCursor));
    }

    /// <summary>
    /// Карточка кейса: исходное сообщение (<c>text</c>, <c>link</c>, <c>postedAt</c>; без автора)
    /// и предложение системы с <c>evidenceQuote</c>. Неизвестный идентификатор — 404.
    /// </summary>
    public async Task<IResult> GetAsync(Guid id, CancellationToken ct)
    {
        var moderationCase = await db.Set<ModerationCase>()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (moderationCase is null)
        {
            return ProblemResponses.NotFound($"Кейс {id} не существует");
        }

        var sourceNames = await ReadNamesAsync(
            "select id, name from sources where id = any(@ids)",
            moderationCase.SourceId is { } sourceId ? [sourceId] : [],
            ct);
        var objectNames = await ReadNamesAsync(
            "select id, name from map_objects where id = any(@ids)",
            moderationCase.MapObjectId is { } objectId ? [objectId] : [],
            ct);

        RawMessageView? rawMessage = null;
        string? excerpt = null;
        if (moderationCase.RawMessageId is { } rawMessageId)
        {
            var stored = await ReadRawMessageAsync(rawMessageId, ct);
            if (stored is { } message)
            {
                rawMessage = new RawMessageView(message.Text, message.PostedAt, Author: null, message.Link);
                excerpt = Excerpt(message.Text);
            }
        }

        var response = new ModerationCaseDetail(
            moderationCase.Id,
            moderationCase.Kind,
            moderationCase.State,
            moderationCase.CreatedAt,
            moderationCase.SourceId is { } caseSourceId ? sourceNames.GetValueOrDefault(caseSourceId) : null,
            moderationCase.MapObjectId is { } caseObjectId ? objectNames.GetValueOrDefault(caseObjectId) : null,
            excerpt,
            moderationCase.SourceId,
            moderationCase.MapObjectId,
            moderationCase.ObservationId,
            rawMessage,
            ParseProposed(moderationCase.Proposed),
            moderationCase.Comment,
            moderationCase.ResolvedAt,
            moderationCase.ResolvedBy);

        return Results.Ok(response);
    }

    /// <summary>Курсор списка — непрозрачная позиция страницы (смещение).</summary>
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

    /// <summary>Идентификатор → название из таблицы другого модуля (только чтение).</summary>
    private async Task<Dictionary<Guid, string>> ReadNamesAsync(
        string sql,
        Guid[] ids,
        CancellationToken ct)
    {
        if (ids.Length == 0)
        {
            return [];
        }

        var connection = db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;

        if (closeAfter)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = sql;
            command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            {
                Value = ids,
            });

            var names = new Dictionary<Guid, string>();

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                names[reader.GetGuid(0)] = reader.GetString(1);
            }

            return names;
        }
        finally
        {
            if (closeAfter)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    /// <summary>Выдержки исходных сообщений для списка (текст обрезается в SQL).</summary>
    private async Task<Dictionary<Guid, string>> ReadMessageExcerptsAsync(
        Guid[] ids,
        CancellationToken ct)
    {
        if (ids.Length == 0)
        {
            return [];
        }

        var connection = db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;

        if (closeAfter)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText =
                $"select id, left(text, {ExcerptLength.ToString(CultureInfo.InvariantCulture)}) "
                + "from raw_messages where id = any(@ids)";
            command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            {
                Value = ids,
            });

            var excerpts = new Dictionary<Guid, string>();

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                excerpts[reader.GetGuid(0)] = reader.GetString(1);
            }

            return excerpts;
        }
        finally
        {
            if (closeAfter)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    /// <summary>
    /// Исходное сообщение кейса. Колонка <c>author</c> намеренно не выбирается: через API
    /// модератора автор сообщения не раскрывается.
    /// </summary>
    private async Task<StoredRawMessage?> ReadRawMessageAsync(Guid id, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;

        if (closeAfter)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = "select text, link, posted_at from raw_messages where id = @id";

            var parameter = command.CreateParameter();
            parameter.ParameterName = "id";
            parameter.Value = id;
            command.Parameters.Add(parameter);

            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            return new StoredRawMessage(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                ReadTimestamp(reader, 2));
        }
        finally
        {
            if (closeAfter)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    /// <summary>
    /// Читает <c>moderation_cases.proposed</c> в схему <c>ProposedObservation</c>. Неизвестные
    /// поля (в том числе <c>reason</c> и <c>mapObjectId</c>) наружу не отдаются.
    /// </summary>
    private static ProposedObservation? ParseProposed(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            Guid? sourceStatusId = null;
            if (root.TryGetProperty("sourceStatusId", out var statusId)
                && statusId.ValueKind == JsonValueKind.String
                && statusId.TryGetGuid(out var parsedStatusId))
            {
                sourceStatusId = parsedStatusId;
            }

            return new ProposedObservation(
                sourceStatusId,
                ReadString(root, "statusCode"),
                ReadNumber(root, "confidence"),
                ReadString(root, "evidenceQuote"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? ReadNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static string Excerpt(string text) =>
        text.Length <= ExcerptLength ? text : text[..ExcerptLength];

    /// <summary>Читает <c>timestamptz</c> как UTC <see cref="DateTimeOffset"/>.</summary>
    private static DateTimeOffset ReadTimestamp(DbDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetFieldValue<DateTime>(ordinal), DateTimeKind.Utc));

    /// <summary>Элемент списка по схеме <c>ModerationCaseSummary</c>.</summary>
    internal sealed record ModerationCaseSummary(
        Guid Id,
        string Kind,
        string State,
        DateTimeOffset CreatedAt,
        string? SourceName,
        string? MapObjectName,
        string? MessageExcerpt);

    /// <summary>Карточка кейса по схеме <c>ModerationCase</c>.</summary>
    internal sealed record ModerationCaseDetail(
        Guid Id,
        string Kind,
        string State,
        DateTimeOffset CreatedAt,
        string? SourceName,
        string? MapObjectName,
        string? MessageExcerpt,
        Guid? SourceId,
        Guid? MapObjectId,
        Guid? ObservationId,
        RawMessageView? RawMessage,
        ProposedObservation? Proposed,
        string? ComplaintText,
        DateTimeOffset? ResolvedAt,
        Guid? ResolvedBy);

    /// <summary>
    /// Сообщение по схеме <c>RawMessageView</c>. <see cref="Author"/> всегда <c>null</c>:
    /// автор сообщения через API не раскрывается.
    /// </summary>
    internal sealed record RawMessageView(
        string Text,
        DateTimeOffset PostedAt,
        string? Author,
        string? Link);

    /// <summary>Предложение системы по схеме <c>ProposedObservation</c>.</summary>
    internal sealed record ProposedObservation(
        Guid? SourceStatusId,
        string? StatusCode,
        double? Confidence,
        string? EvidenceQuote);

    /// <summary>Прочитанное исходное сообщение (без автора).</summary>
    private sealed record StoredRawMessage(string Text, string? Link, DateTimeOffset PostedAt);
}
