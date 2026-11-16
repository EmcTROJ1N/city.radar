using System.Data;
using System.Data.Common;
using System.Text.Json;

using BuildingBlocks.Persistence;
using BuildingBlocks.Web;

using Catalog.Contracts;

using IdentityAccess.Contracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Moderation.Contracts;
using Moderation.Core.Domain;

namespace Moderation.Core.Endpoints;

/// <summary>
/// Жалобы пользователей на статус фиксации:
/// <c>POST /objects/{id}/complaints</c> создаёт кейс <c>complaint</c> в очереди модерации.
/// </summary>
/// <remarks>
/// <para>
/// Право — <c>observation.create</c>: пожаловаться может любой зарегистрированный пользователь,
/// аноним получает 401. Тело запроса по контракту — <c>ComplaintRequest</c>
/// (<c>text</c>, необязательный <c>observationId</c>); <c>comment</c> принимается как
/// совместимый псевдоним поля текста.
/// </para>
/// <para>
/// Неактивная или несуществующая фиксация — 404 (жалоба не должна раскрывать существование
/// скрытой фиксации). Текст жалобы не длиннее <c>comment_max_length</c> из
/// <see cref="IGlobalSettings"/> и проходит проверку текста — иначе 422.
/// </para>
/// <para>
/// Rate limit как у отметок (<c>observation_rate_per_hour</c>) в этом потоке не реализован:
/// точка расширения — лимитер (<c>IRateLimiter</c> в <c>BuildingBlocks.Web</c>).
/// </para>
/// </remarks>
public static class ComplaintsEndpoints
{
    private const string BodyField = "body";
    private const string TextField = "text";

    private static readonly HashSet<string> AllowedFields =
        new(StringComparer.Ordinal) { "text", "comment", "observationId" };

    /// <summary>Регистрирует жалобы внутри текущего префикса API (например, <c>/api/v1</c>).</summary>
    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapPost("/objects/{id:guid}/complaints", CreateAsync)
            .RequireAuthorization(Permissions.ObservationCreate);
    }

    private static async Task<IResult> CreateAsync(
        Guid id,
        HttpContext context,
        IMapObjectDirectory mapObjects,
        ICurrentUser currentUser,
        IGlobalSettings settings,
        IModerationCaseCreator creator,
        MayakDbContext db,
        CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Не авторизован",
                detail: "Войдите, чтобы пожаловаться на статус",
                type: "about:blank");
        }

        var objectInfo = await mapObjects.GetInfoAsync(id, ct);
        if (objectInfo is null || !objectInfo.IsActive)
        {
            return ProblemResponses.NotFound($"Фиксация {id} не существует");
        }

        using var document = await TryReadObjectAsync(context.Request, ct);
        if (document is null)
        {
            return ProblemResponses.Validation(
                new ValidationError(BodyField, "Ожидается JSON-объект жалобы"));
        }

        var errors = new List<ValidationError>();
        var request = ReadRequest(document.RootElement, errors);
        if (request is null)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        var snapshot = await settings.GetAsync(ct);
        if (request.Text is { Length: > 0 } text)
        {
            if (text.Length > snapshot.CommentMaxLength)
            {
                errors.Add(new ValidationError(
                    TextField,
                    $"Текст жалобы длиннее {snapshot.CommentMaxLength} символов"));
            }

            if (!TextValidator.IsValid(text))
            {
                errors.Add(new ValidationError(TextField, TextValidator.InvalidCharactersMessage));
            }
        }

        if (request.ObservationId is { } observationId
            && !await ObservationBelongsToObjectAsync(db, observationId, id, ct))
        {
            errors.Add(new ValidationError(
                "observationId",
                "Наблюдение не относится к этой фиксации"));
        }

        if (errors.Count > 0)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        var caseId = await creator.CreateAsync(
            new NewModerationCase(
                ModerationCaseKinds.Complaint,
                SourceId: null,
                RawMessageId: null,
                MapObjectId: id,
                ObservationId: request.ObservationId,
                ReporterId: userId,
                Comment: request.Text,
                Proposed: null),
            ct);

        // IModerationCaseCreator не сохраняет изменения: жалоба фиксируется в этом запросе.
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/v1/moderation/cases/{caseId}",
            new ComplaintCreated(caseId));
    }

    /// <summary>Читает тело как JSON-объект; <c>null</c> — тело не объект или не JSON.</summary>
    private static async Task<JsonDocument?> TryReadObjectAsync(HttpRequest request, CancellationToken ct)
    {
        try
        {
            var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Разбирает <c>ComplaintRequest</c>: <c>text</c> (псевдоним — <c>comment</c>) и
    /// необязательный <c>observationId</c>. Неизвестные поля отклоняются (422).
    /// </summary>
    private static ComplaintInput? ReadRequest(JsonElement root, List<ValidationError> errors)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!AllowedFields.Contains(property.Name))
            {
                errors.Add(new ValidationError(property.Name, "Неизвестное поле"));
            }
        }

        var hasText = root.TryGetProperty("text", out var textElement);
        var hasComment = root.TryGetProperty("comment", out var commentElement);

        if (hasText && hasComment)
        {
            errors.Add(new ValidationError(
                TextField,
                "Укажите текст жалобы только в одном поле: text или comment"));
        }

        var text = ReadOptionalString(hasText ? textElement : commentElement, errors, TextField);

        Guid? observationId = null;
        if (root.TryGetProperty("observationId", out var observationElement))
        {
            if (observationElement.ValueKind == JsonValueKind.Null)
            {
                errors.Add(new ValidationError("observationId", "Поле не может быть null"));
            }
            else if (observationElement.ValueKind == JsonValueKind.String
                && observationElement.TryGetGuid(out var parsed))
            {
                observationId = parsed;
            }
            else
            {
                errors.Add(new ValidationError("observationId", "Ожидается UUID"));
            }
        }

        return errors.Count == 0 ? new ComplaintInput(text, observationId) : null;
    }

    private static string? ReadOptionalString(
        JsonElement element,
        List<ValidationError> errors,
        string field)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            errors.Add(new ValidationError(field, "Ожидается строка"));

            return null;
        }

        var text = element.GetString();
        if (!TextValidator.IsStorable(text))
        {
            errors.Add(new ValidationError(field, TextValidator.UnstorableMessage));

            return null;
        }

        return text;
    }

    /// <summary>Жалоба ссылается только на наблюдение этой же фиксации.</summary>
    private static async Task<bool> ObservationBelongsToObjectAsync(
        MayakDbContext db,
        Guid observationId,
        Guid mapObjectId,
        CancellationToken ct)
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
            command.CommandText =
                "select count(*) from observations where id = @id and map_object_id = @map_object";
            AddParameter(command, "id", observationId);
            AddParameter(command, "map_object", mapObjectId);

            var count = await command.ExecuteScalarAsync(ct);

            return Convert.ToInt64(count, System.Globalization.CultureInfo.InvariantCulture) > 0;
        }
        finally
        {
            if (closeAfter)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>Разобранное тело жалобы.</summary>
    private sealed record ComplaintInput(string? Text, Guid? ObservationId);

    /// <summary>Ответ 201 по схеме <c>ComplaintCreated</c>.</summary>
    private sealed record ComplaintCreated(Guid CaseId);
}
