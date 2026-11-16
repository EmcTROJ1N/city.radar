using System.Text.Json;

using Microsoft.AspNetCore.Http;

namespace BuildingBlocks.Web;

/// <summary>Ошибка одного поля в <c>ValidationProblem</c> (схема <c>ValidationProblem</c>).</summary>
/// <param name="Field">Имя поля запроса; для вложенных элементов — <c>roles[1]</c>.</param>
/// <param name="Message">Текст ошибки для пользователя.</param>
public sealed record ValidationError(string Field, string Message);

/// <summary>
/// Ответы 422 по схеме <c>ValidationProblem</c>: <c>errors</c> — массив объектов
/// <c>{ field, message }</c>, а не словарь, как в <c>Results.ValidationProblem</c>.
/// </summary>
public static partial class ProblemResponses
{
    private static readonly JsonSerializerOptions ValidationJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>422 с массивом ошибок полей (схема <c>ValidationProblem</c>).</summary>
    public static IResult Validation(params ValidationError[] errors) =>
        new ValidationProblemResult(errors);

    /// <summary>Записывает 422 <c>application/problem+json</c> в уже начатый или ещё пустой ответ.</summary>
    public static async Task WriteValidationAsync(
        HttpResponse response,
        IReadOnlyList<ValidationError> errors,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(errors);

        if (response.HasStarted)
        {
            return;
        }

        response.Clear();
        response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        response.ContentType = "application/problem+json";

        var body = new ValidationBody(
            "about:blank",
            "Ошибка валидации",
            StatusCodes.Status422UnprocessableEntity,
            "Запрос содержит недопустимые значения",
            errors);

        await JsonSerializer.SerializeAsync(response.Body, body, ValidationJsonOptions, cancellationToken);
    }

    /// <summary>400 для некорректных параметров запроса (не схема запроса, а сам вызов).</summary>
    public static IResult BadRequest(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Некорректный запрос",
            detail: detail,
            type: "about:blank");

    /// <summary>404 по схеме <c>NotFound</c> контракта.</summary>
    public static IResult NotFound(string detail = "Запрошенный объект не существует") =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Не найдено",
            detail: detail,
            type: "about:blank");

    /// <summary>409 по схеме <c>Conflict</c> контракта: дубль, ссылочная целостность.</summary>
    public static IResult Conflict(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Конфликт",
            detail: detail,
            type: "about:blank");

    private sealed record ValidationBody(
        string Type,
        string Title,
        int Status,
        string Detail,
        IReadOnlyList<ValidationError> Errors);

    private sealed class ValidationProblemResult(IReadOnlyList<ValidationError> errors) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) =>
            WriteValidationAsync(httpContext.Response, errors, httpContext.RequestAborted);
    }
}
