using System.Text.Json;

using BuildingBlocks.Persistence;
using BuildingBlocks.Web;

using IdentityAccess.Contracts;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

using Moderation.Core.Domain;
using Moderation.Core.Services;

namespace Moderation.Core.Handlers;

/// <summary>Тело <c>POST /moderation/cases/{id}/reject</c>.</summary>
/// <param name="Reason">Причина отклонения; попадает в аудит.</param>
internal sealed record RejectCaseRequest(string? Reason);

/// <summary>
/// Отклонение кейса модерации: наблюдение не создаётся,
/// кейс закрывается состоянием <c>rejected</c>, действие пишется в аудит.
/// </summary>
internal sealed class RejectCaseHandler(
    MayakDbContext db,
    IAuditLog audit,
    ICurrentUser currentUser,
    TimeProvider time,
    ModerationCaseService caseService)
{
    private const string AuditAction = "moderation.case.reject";
    private const string EntityType = "moderation_case";
    private const int ReasonMaxLength = 1000;

    public async Task<IResult> HandleAsync(
        Guid id,
        RejectCaseRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (currentUser.UserId is not { } userId)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Не авторизован",
                detail: "Войдите, чтобы обработать кейс",
                type: "about:blank");
        }

        var reason = request.Reason;
        if (reason is not null)
        {
            if (reason.Length > ReasonMaxLength)
            {
                return ProblemResponses.Validation(
                    new ValidationError(
                        "reason",
                        $"Причина длиннее {ReasonMaxLength} символов"));
            }

            if (!TextValidator.IsValid(reason))
            {
                return ProblemResponses.Validation(
                    new ValidationError("reason", TextValidator.InvalidCharactersMessage));
            }
        }

        var moderationCase = await db.Set<ModerationCase>()
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (moderationCase is null)
        {
            return ProblemResponses.NotFound($"Кейс {id} не существует");
        }

        if (moderationCase.State != ModerationCaseStates.Open)
        {
            return ProblemResponses.Conflict(
                $"Кейс {id} уже обработан: состояние {moderationCase.State}");
        }

        var now = time.GetUtcNow();
        var before = new { state = moderationCase.State };

        moderationCase.State = ModerationCaseStates.Rejected;
        moderationCase.ResolvedAt = now;
        moderationCase.ResolvedBy = userId;
        moderationCase.Resolution = JsonSerializer.Serialize(
            new { action = "reject", reason },
            JsonSerializerOptions.Web);

        await audit.WriteAsync(
            AuditAction,
            EntityType,
            moderationCase.Id.ToString(),
            before,
            new
            {
                state = moderationCase.State,
                reason,
                resolvedAt = now,
                resolvedBy = userId,
            },
            reason: reason,
            ct: ct);

        await db.SaveChangesAsync(ct);

        return await caseService.GetAsync(id, ct);
    }
}
