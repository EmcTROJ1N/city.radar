using System.Text.Json;

using BuildingBlocks.Web;

using Catalog.Contracts;

using IdentityAccess.Contracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Observations.Application;

namespace Observations.Infrastructure;

/// <summary>
/// Эндпоинты ядра наблюдений (4.5): <c>POST /observations</c> — ручная отметка,
/// <c>POST /observations/{id}/retract</c> и <c>POST /observations/retract-bulk</c> — отзыв,
/// <c>GET /objects/{id}/observations</c> — публичный список отметок фиксации.
/// </summary>
public static class ObservationsEndpoints
{
    private const string BodyField = "body";

    private static readonly HashSet<string> CreateFields =
        new(StringComparer.Ordinal)
        {
            "mapObjectId", "sourceId", "sourceStatusId", "comment", "location", "geo",
        };

    private static readonly HashSet<string> RetractFields =
        new(StringComparer.Ordinal) { "reason" };

    private static readonly HashSet<string> BulkRetractFields =
        new(StringComparer.Ordinal) { "reason", "reporterId", "sourceId", "from", "to" };

    /// <summary>Регистрирует эндпоинты наблюдений внутри текущего префикса (например, <c>/api/v1</c>).</summary>
    public static IEndpointRouteBuilder MapObservationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/observations");

        group
            .MapPost(string.Empty, CreateAsync)
            .RequireAuthorization(Permissions.ObservationCreate)
            // Rate limit: применяется до обработчика и до обращения к БД,
            // превышение — 429 + Retry-After.
            .AddEndpointFilter<ObservationRateLimitFilter>();

        group
            .MapPost("/{id:guid}/retract", RetractAsync)
            .RequireAuthorization(Permissions.ObservationRetract);

        group
            .MapPost("/retract-bulk", RetractBulkAsync)
            .RequireAuthorization(Permissions.ObservationRetract);

        // Публичный список: право map.read есть у анонима, поэтому авторизация не требуется.
        endpoints.MapGet("/objects/{id:guid}/observations", ListAsync);

        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        CreateObservationHandler handler,
        ObservationQueries queries,
        CancellationToken ct)
    {
        using var document = await TryReadObjectAsync(context.Request, ct);
        if (document is null)
        {
            return ProblemResponses.Validation(
                new ValidationError(BodyField, "Ожидается JSON-объект отметки"));
        }

        var errors = new List<ValidationError>();
        var command = ReadCreateRequest(document.RootElement, errors);
        if (command is null)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        CreatedObservation created;
        try
        {
            created = await handler.HandleAsync(command, ct);
        }
        catch (ObservationRequestException exception)
        {
            return ToProblem(exception);
        }

        var status = await queries.GetCurrentStatusAsync(created.MapObjectId, ct);

        var observation = new PublicObservationView(
            created.Id,
            created.Status,
            created.ObservedAt,
            created.ExpiresAt,
            created.SourceType,
            SourceName: null,
            Text: created.Comment,
            Confidence: null,
            GeoDistanceM: created.GeoDistanceM);

        return Results.Created(
            $"/api/v1/observations/{created.Id}",
            new CreateObservationResponse(observation, status));
    }

    private static async Task<IResult> RetractAsync(
        HttpContext context,
        Guid id,
        RetractObservationHandler handler,
        CancellationToken ct)
    {
        using var document = await TryReadObjectAsync(context.Request, ct);
        if (document is null)
        {
            return ProblemResponses.Validation(
                new ValidationError(BodyField, "Ожидается JSON-объект с причиной отзыва"));
        }

        var errors = new List<ValidationError>();
        RejectUnknownFields(document.RootElement, RetractFields, errors);
        var reason = ReadOptionalString(document.RootElement, "reason", errors);
        if (errors.Count > 0)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        try
        {
            var status = await handler.HandleAsync(
                id,
                new RetractObservationCommand(reason),
                ct);

            return Results.Ok(new RetractResponse(status));
        }
        catch (ObservationRequestException exception)
        {
            return ToProblem(exception);
        }
    }

    private static async Task<IResult> RetractBulkAsync(
        HttpContext context,
        RetractBulkHandler handler,
        CancellationToken ct)
    {
        using var document = await TryReadObjectAsync(context.Request, ct);
        if (document is null)
        {
            return ProblemResponses.Validation(
                new ValidationError(BodyField, "Ожидается JSON-объект с фильтрами отзыва"));
        }

        var errors = new List<ValidationError>();
        var root = document.RootElement;
        RejectUnknownFields(root, BulkRetractFields, errors);
        // Контракт не допускает null в необязательных фильтрах: отсутствие поля и null различаются.
        RejectExplicitNull(root, ["reporterId", "sourceId", "from", "to"], errors);

        var reason = ReadOptionalString(root, "reason", errors);
        var reporterId = ReadOptionalGuid(root, "reporterId", errors);
        var sourceId = ReadOptionalGuid(root, "sourceId", errors);
        var from = ReadOptionalTimestamp(root, "from", errors);
        var to = ReadOptionalTimestamp(root, "to", errors);

        if (errors.Count > 0)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        try
        {
            var count = await handler.HandleAsync(
                new BulkRetractCommand(reason, reporterId, sourceId, from, to),
                ct);

            return Results.Ok(new BulkRetractResponse(count));
        }
        catch (ObservationRequestException exception)
        {
            return ToProblem(exception);
        }
    }

    private static async Task<IResult> ListAsync(
        Guid id,
        int? limit,
        string? cursor,
        ObservationQueries queries,
        IMapObjectDirectory mapObjects,
        CancellationToken ct)
    {
        if (!PageRequest.TryCreate(limit, cursor, out var page, out var failure))
        {
            return failure;
        }

        var info = await mapObjects.GetInfoAsync(id, ct);
        if (info is null || !info.IsActive)
        {
            return ProblemResponses.NotFound($"Фиксация {id} не существует");
        }

        if (!TryReadOffset(page, out var offset))
        {
            return ProblemResponses.BadRequest("Некорректный курсор");
        }

        var batch = await queries.ListAsync(id, offset, page.Limit, ct);
        var nextCursor = batch.HasMore
            ? PageRequest.EncodeCursor(offset + batch.Items.Count)
            : null;

        return Results.Ok(new Page<PublicObservationView>(batch.Items, nextCursor));
    }

    private static IResult ToProblem(ObservationRequestException exception) =>
        exception.StatusCode switch
        {
            404 => ProblemResponses.NotFound(exception.Message),
            409 => ProblemResponses.Conflict(exception.Message),
            401 => Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Не авторизован",
                detail: exception.Message,
                type: "about:blank"),
            _ => ProblemResponses.Validation(
                new ValidationError(exception.Field, exception.Message)),
        };

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

    /// <summary>
    /// Разбирает <c>CreateObservationRequest</c>: <c>mapObjectId</c>, <c>sourceId</c> и
    /// <c>sourceStatusId</c> обязательны; клиентское <c>observedAt</c> контрактом не
    /// предусмотрено и отклоняется как неизвестное поле. Геолокация — <c>location</c>
    /// по контракту; <c>geo</c> принимается как совместимый псевдоним.
    /// </summary>
    private static CreateObservationCommand? ReadCreateRequest(
        JsonElement root,
        List<ValidationError> errors)
    {
        RejectUnknownFields(root, CreateFields, errors);

        var mapObjectId = ReadRequiredGuid(root, "mapObjectId", errors);
        var sourceId = ReadRequiredGuid(root, "sourceId", errors);
        var sourceStatusId = ReadRequiredGuid(root, "sourceStatusId", errors);
        var comment = ReadOptionalString(root, "comment", errors);

        var hasLocation = root.TryGetProperty("location", out var location)
            && location.ValueKind != JsonValueKind.Null;
        var hasGeo = root.TryGetProperty("geo", out var geo)
            && geo.ValueKind != JsonValueKind.Null;

        if (hasLocation && hasGeo)
        {
            errors.Add(new ValidationError("location", "Укажите только одно поле геолокации"));
        }

        double? latitude = null;
        double? longitude = null;

        if (hasLocation)
        {
            ReadCoordinates(location, "location", errors, out latitude, out longitude);
        }
        else if (hasGeo)
        {
            ReadCoordinates(geo, "geo", errors, out latitude, out longitude);
        }

        return errors.Count == 0
            ? new CreateObservationCommand(
                mapObjectId!.Value,
                sourceId!.Value,
                sourceStatusId!.Value,
                comment,
                latitude,
                longitude)
            : null;
    }

    /// <summary>
    /// Координаты геолокации: <c>lat</c> в −90…90, <c>lon</c> в −180…180. <c>accuracy</c>
    /// допускается контрактом, но для расчёта расстояния не нужен и игнорируется.
    /// </summary>
    private static void ReadCoordinates(
        JsonElement value,
        string field,
        List<ValidationError> errors,
        out double? latitude,
        out double? longitude)
    {
        latitude = null;
        longitude = null;

        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new ValidationError(field, "Ожидается объект с полями lat и lon"));

            return;
        }

        var lat = 0d;
        var lon = 0d;

        var hasLatitude = value.TryGetProperty("lat", out var latElement)
            && latElement.ValueKind == JsonValueKind.Number
            && latElement.TryGetDouble(out lat)
            && double.IsFinite(lat);
        var hasLongitude = value.TryGetProperty("lon", out var lonElement)
            && lonElement.ValueKind == JsonValueKind.Number
            && lonElement.TryGetDouble(out lon)
            && double.IsFinite(lon);

        if (!hasLatitude || !hasLongitude)
        {
            errors.Add(new ValidationError(field, "Ожидаются числовые lat и lon"));

            return;
        }

        if (lat is < -90 or > 90)
        {
            errors.Add(new ValidationError($"{field}.lat", "Широта должна быть от -90 до 90"));

            return;
        }

        if (lon is < -180 or > 180)
        {
            errors.Add(new ValidationError($"{field}.lon", "Долгота должна быть от -180 до 180"));

            return;
        }

        latitude = lat;
        longitude = lon;
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

    /// <summary>
    /// Отклоняет явный <c>null</c> в необязательном поле, которое контракт объявляет
    /// не-nullable: отсутствие поля допустимо, <c>null</c> — нет.
    /// </summary>
    private static void RejectExplicitNull(
        JsonElement root,
        string[] fields,
        List<ValidationError> errors)
    {
        foreach (var field in fields)
        {
            if (root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Null)
            {
                errors.Add(new ValidationError(field, "Поле не может быть null"));
            }
        }
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

    private static Guid? ReadRequiredGuid(
        JsonElement root,
        string field,
        List<ValidationError> errors)
    {
        if (!root.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            errors.Add(new ValidationError(field, $"Поле {field} обязательно"));

            return null;
        }

        if (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out var id))
        {
            errors.Add(new ValidationError(field, "Ожидается uuid"));

            return null;
        }

        return id;
    }

    /// <summary>Необязательный <c>uuid</c>: отсутствие или <c>null</c> — <c>null</c>, иное — ошибка.</summary>
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

    /// <summary>Необязательная метка времени ISO 8601 (<c>date-time</c> контракта).</summary>
    private static DateTimeOffset? ReadOptionalTimestamp(
        JsonElement root,
        string field,
        List<ValidationError> errors)
    {
        if (!root.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                value.GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            errors.Add(new ValidationError(field, "Ожидается дата и время в формате ISO 8601"));

            return null;
        }

        return timestamp;
    }

    /// <summary>Курсор публичного списка — непрозрачная позиция страницы (смещение).</summary>
    private static bool TryReadOffset(PageRequest page, out int offset)
    {
        offset = 0;

        if (!page.HasCursor)
        {
            return true;
        }

        return int.TryParse(
                page.CursorPayload,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out offset)
            && offset >= 0;
    }

    /// <summary>Ответ <c>CreateObservationResponse</c>: отметка и пересчитанный статус.</summary>
    private sealed record CreateObservationResponse(
        PublicObservationView Observation,
        CurrentStatusViewDto? Status);

    /// <summary>Ответ <c>RetractResult</c>: статус фиксации после пересчёта (или <c>null</c>).</summary>
    private sealed record RetractResponse(CurrentStatusViewDto? Status);

    /// <summary>Ответ <c>BulkRetractResult</c>: сколько наблюдений отозвано.</summary>
    private sealed record BulkRetractResponse(int RetractedCount);
}
