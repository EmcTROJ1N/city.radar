using BuildingBlocks.Persistence;

using IdentityAccess.Contracts;

using Microsoft.EntityFrameworkCore;

using Observations.Domain;

namespace Observations.Application;

/// <summary>Команда отзыва одного наблюдения (<c>POST /observations/{id}/retract</c>).</summary>
/// <param name="Reason">Причина отзыва — обязательна и уходит в аудит.</param>
public sealed record RetractObservationCommand(string? Reason);

/// <summary>
/// Отзыв одного наблюдения.
/// </summary>
/// <remarks>
/// Отзыв — единственное допустимое изменение наблюдения. Порядок записи: наблюдение помечается
/// отозванным, аудит-запись добавляется в change tracker, затем <c>SaveChanges</c> фиксирует оба
/// изменения, после чего пересчёт читает уже сохранённое состояние и обновляет
/// <c>current_statuses</c> в той же транзакции. Любой сбой (в том числе пересчёта) откатывает
/// и отзыв, и аудит.
/// </remarks>
public sealed class RetractObservationHandler(
    MayakDbContext db,
    IObservationRepository repository,
    IStatusRecalculator recalculator,
    IObservationReadStore readStore,
    ICurrentUser currentUser,
    IAuditLog audit,
    TimeProvider time)
{
    /// <summary>Максимальная длина причины отзыва (контракт <c>RetractRequest.reason</c>).</summary>
    public const int MaxReasonLength = 1000;

    /// <summary>
    /// Отзывает наблюдение, пересчитывает проекцию фиксации и пишет аудит.
    /// </summary>
    /// <param name="id">Идентификатор наблюдения.</param>
    /// <param name="command">Причина отзыва.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Статус фиксации после пересчёта; <c>null</c> — «неизвестно».</returns>
    /// <exception cref="ObservationRequestException">
    /// 401 — нет пользователя, 404 — наблюдения нет, 409 — уже отозвано, 422 — причина не задана.
    /// </exception>
    public async Task<CurrentStatusViewDto?> HandleAsync(
        Guid id,
        RetractObservationCommand command,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.UserId is not { } userId)
        {
            throw new ObservationRequestException(
                401,
                "authorization",
                "Войдите, чтобы отозвать наблюдение");
        }

        var reason = ValidateReason(command.Reason);

        var observation = await repository.GetAsync(id, ct)
            ?? throw new ObservationRequestException(
                404,
                "id",
                $"Наблюдение {id} не существует");

        if (observation.IsRetracted)
        {
            throw new ObservationRequestException(409, "id", "Наблюдение уже отозвано");
        }

        var now = time.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        try
        {
            try
            {
                await repository.RetractAsync(id, userId, reason, now, ct);
            }
            catch (DomainException exception)
            {
                // Гонка двух одновременных отзывов: второй видит уже отозванную строку.
                throw new ObservationRequestException(409, "id", exception.Message);
            }

            var before = new
            {
                mapObjectId = observation.MapObjectId,
                sourceId = observation.SourceId,
                sourceStatusId = observation.SourceStatusId,
                sourceType = ToDb(observation.Kind),
                observedAt = observation.ObservedAt,
                isRetracted = false,
            };

            await audit.WriteAsync(
                "observation.retract",
                "observation",
                id.ToString(),
                before,
                after: new
                {
                    mapObjectId = observation.MapObjectId,
                    sourceId = observation.SourceId,
                    sourceStatusId = observation.SourceStatusId,
                    sourceType = ToDb(observation.Kind),
                    observedAt = observation.ObservedAt,
                    isRetracted = true,
                    retractedAt = now,
                    retractedBy = userId,
                    reason,
                },
                reason: reason,
                ct: ct);

            await db.SaveChangesAsync(ct);

            //: пересчёт видит отозванное наблюдение и пишет проекцию в этой же транзакции.
            await recalculator.RecalculateAsync(observation.MapObjectId, now, ct);

            var status = await readStore.GetCurrentStatusAsync(observation.MapObjectId, ct);

            await transaction.CommitAsync(ct);

            return status;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);

            throw;
        }
    }

    /// <summary>Причина обязательна: пустая строка, пробелы и длина больше 1000 — 422.</summary>
    internal static string ValidateReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ObservationRequestException(
                422,
                "reason",
                "Причина отзыва обязательна");
        }

        var trimmed = reason.Trim();
        if (trimmed.Length > MaxReasonLength)
        {
            throw new ObservationRequestException(
                422,
                "reason",
                $"Причина отзыва длиннее {MaxReasonLength} символов");
        }

        return trimmed;
    }

    private static string ToDb(SourceKind kind) => kind switch
    {
        SourceKind.Manual => "manual",
        SourceKind.Moderator => "moderator",
        SourceKind.Telegram => "telegram",
        SourceKind.Max => "max",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный тип источника."),
    };
}
