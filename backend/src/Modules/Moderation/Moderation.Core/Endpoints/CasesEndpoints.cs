using System.Text.Json;

using BuildingBlocks.Web;

using IdentityAccess.Contracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Moderation.Core.Handlers;
using Moderation.Core.Services;

namespace Moderation.Core.Endpoints;

/// <summary>
/// Очередь модерации (Tasks 6.1–6.2): список и карточка кейса,
/// принятие и отклонение. Все методы требуют право <c>moderation.queue.process</c>:
/// аноним получает 401, пользователь без права — 403.
/// </summary>
public static class CasesEndpoints
{
    private const string BodyField = "body";

    private static readonly HashSet<string> ApproveFields =
        new(StringComparer.Ordinal)
        {
            "mapObjectId", "mapObjectIds", "sourceStatusId",
        };

    private static readonly HashSet<string> RejectFields =
        new(StringComparer.Ordinal)
        {
            "reason",
        };

    /// <summary>Регистрирует эндпоинты модерации внутри текущего префикса API.</summary>
    public static IEndpointRouteBuilder MapModerationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints
            .MapGroup("/moderation")
            .RequireAuthorization(Permissions.ModerationQueueProcess);

        group.MapGet("/cases", ListAsync);
        group.MapGet("/cases/{id:guid}", GetAsync);
        group.MapPost("/cases/{id:guid}/approve", ApproveAsync);
        group.MapPost("/cases/{id:guid}/reject", RejectAsync);

        // Жалобы живут вне /moderation, но подключаются тем же вызовом.
        ComplaintsEndpoints.Map(endpoints);

        return endpoints;
    }

    private static Task<IResult> ListAsync(
        string? kind,
        string? state,
        int? limit,
        string? cursor,
        ModerationCaseService service,
        CancellationToken ct)
    {
        if (!PageRequest.TryCreate(limit, cursor, out var page, out var failure))
        {
            return Task.FromResult(failure);
        }

        return service.ListAsync(kind, state, page, ct);
    }

    private static Task<IResult> GetAsync(
        Guid id,
        ModerationCaseService service,
        CancellationToken ct) =>
        service.GetAsync(id, ct);

    private static async Task<IResult> ApproveAsync(
        Guid id,
        HttpContext context,
        ApproveCaseHandler handler,
        CancellationToken ct)
    {
        using var document = await TryReadObjectAsync(context.Request, ct);
        if (document is null)
        {
            return ProblemResponses.Validation(
                new ValidationError(BodyField, "Ожидается JSON-объект решения по кейсу"));
        }

        var errors = new List<ValidationError>();
        var request = ReadApproveRequest(document.RootElement, errors);
        if (request is null)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        return await handler.HandleAsync(id, request, ct);
    }

    private static async Task<IResult> RejectAsync(
        Guid id,
        HttpContext context,
        RejectCaseHandler handler,
        CancellationToken ct)
    {
        using var document = await TryReadObjectAsync(context.Request, ct);
        if (document is null)
        {
            return ProblemResponses.Validation(
                new ValidationError(BodyField, "Ожидается JSON-объект решения по кейсу"));
        }

        var errors = new List<ValidationError>();
        var request = ReadRejectRequest(document.RootElement, errors);
        if (request is null)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        return await handler.HandleAsync(id, request, ct);
    }

    /// <summary>
    /// Разбирает <c>ApproveCaseRequest</c>. Контракт объявляет одиночный <c>mapObjectId</c>,
    /// а для выбора нескольких фиксаций принимается <c>mapObjectIds</c>; оба сразу — ошибка.
    /// </summary>
    private static ApproveCaseRequest? ReadApproveRequest(
        JsonElement root,
        List<ValidationError> errors)
    {
        RejectUnknownFields(root, ApproveFields, errors);

        var singular = ReadOptionalGuid(root, "mapObjectId", errors);
        var plural = ReadOptionalGuidArray(root, "mapObjectIds", errors);

        if (singular is not null && plural is not null)
        {
            errors.Add(new ValidationError(
                "mapObjectIds",
                "Укажите только одно поле выбора фиксаций"));
        }

        IReadOnlyList<Guid> mapObjectIds = plural
            ?? (singular is { } single ? [single] : []);

        var sourceStatusId = ReadOptionalGuid(root, "sourceStatusId", errors);

        return errors.Count == 0
            ? new ApproveCaseRequest(mapObjectIds, sourceStatusId)
            : null;
    }

    private static RejectCaseRequest? ReadRejectRequest(
        JsonElement root,
        List<ValidationError> errors)
    {
        RejectUnknownFields(root, RejectFields, errors);

        var reason = ReadOptionalString(root, "reason", errors);

        return errors.Count == 0 ? new RejectCaseRequest(reason) : null;
    }

    /// <summary>Читает тело запроса как JSON-объект; <c>null</c> — тело не объект или не JSON.</summary>
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

    private static void RejectUnknownFields(
        JsonElement root,
        HashSet<string> allowed,
        List<ValidationError> errors)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                errors.Add(new ValidationError(property.Name, "Неизвестное поле"));
            }
        }
    }

    private static Guid? ReadOptionalGuid(
        JsonElement root,
        string field,
        List<ValidationError> errors)
    {
        if (!root.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out var id))
        {
            errors.Add(new ValidationError(field, "Ожидается uuid"));

            return null;
        }

        return id;
    }

    private static IReadOnlyList<Guid>? ReadOptionalGuidArray(
        JsonElement root,
        string field,
        List<ValidationError> errors)
    {
        if (!root.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new ValidationError(field, "Ожидается массив uuid"));

            return null;
        }

        var result = new List<Guid>();

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || !Guid.TryParse(item.GetString(), out var id))
            {
                errors.Add(new ValidationError(field, "Ожидается массив uuid"));

                return null;
            }

            result.Add(id);
        }

        return result;
    }

    private static string? ReadOptionalString(
        JsonElement root,
        string field,
        List<ValidationError> errors)
    {
        if (!root.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add(new ValidationError(field, "Ожидается строка"));

            return null;
        }

        var text = value.GetString();
        if (!TextValidator.IsStorable(text))
        {
            errors.Add(new ValidationError(field, TextValidator.UnstorableMessage));

            return null;
        }

        return text;
    }
}
