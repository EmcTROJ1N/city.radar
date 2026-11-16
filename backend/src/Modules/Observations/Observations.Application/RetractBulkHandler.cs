using BuildingBlocks.Persistence;

using IdentityAccess.Contracts;

using Microsoft.EntityFrameworkCore;

namespace Observations.Application;

/// <summary>
/// Фильтр пакетного отзыва: автор отметок, источник и/или период по
/// <c>observed_at</c>. Хотя бы один фильтр обязателен — иначе операция отозвала бы всё.
/// </summary>
/// <param name="ReporterId">Автор отметок (для пользовательских и модераторских наблюдений).</param>
/// <param name="SourceId">Источник наблюдения.</param>
/// <param name="From">Нижняя граница периода по <c>observed_at</c> включительно.</param>
/// <param name="To">Верхняя граница периода по <c>observed_at</c> включительно.</param>
public sealed record BulkRetractFilter(
    Guid? ReporterId,
    Guid? SourceId,
    DateTimeOffset? From,
    DateTimeOffset? To)
{
    /// <summary>Задан хотя бы один отбор.</summary>
    public bool HasAny =>
        ReporterId is not null || SourceId is not null || From is not null || To is not null;
}

/// <summary>Строка отозванного наблюдения: идентификатор и фиксация для пересчёта.</summary>
/// <param name="Id">Идентификатор наблюдения.</param>
/// <param name="MapObjectId">Фиксация, которую нужно пересчитать.</param>
public sealed record RetractedObservationRow(Guid Id, Guid MapObjectId);

/// <summary>Команда пакетного отзыва (<c>POST /observations/retract-bulk</c>).</summary>
/// <param name="Reason">Причина отзыва — обязательна.</param>
/// <param name="ReporterId">Автор отметок.</param>
/// <param name="SourceId">Источник.</param>
/// <param name="From">Начало периода по времени наблюдения.</param>
/// <param name="To">Конец периода по времени наблюдения.</param>
public sealed record BulkRetractCommand(
    string? Reason,
    Guid? ReporterId,
    Guid? SourceId,
    DateTimeOffset? From,
    DateTimeOffset? To);

/// <summary>
/// Пакетный отзыв наблюдений по пользователю, источнику и/или периоду
///).
/// </summary>
/// <remarks>
/// Операция выполняется одной транзакцией: отзыв строк, одна аудит-запись на всю операцию и
/// пересчёт проекции каждой затронутой фиксации. Отзываются только ещё не отозванные
/// наблюдения. Размер операции ограничен <see cref="MaxBatchSize"/>: при большем числе
/// кандидатов запрос отклоняется (422) — фильтр нужно сузить, чтобы нельзя было случайно
/// выключить всю историю региона.
/// </remarks>
public sealed class RetractBulkHandler(
    MayakDbContext db,
    IObservationRepository repository,
    IStatusRecalculator recalculator,
    ICurrentUser currentUser,
    IAuditLog audit,
    TimeProvider time)
{
    /// <summary>Максимальное число наблюдений в одной операции пакетного отзыва.</summary>
    public const int MaxBatchSize = 10_000;

    /// <summary>
    /// Отзывает наблюдения, подходящие под фильтр, и пересчитывает проекции затронутых фиксаций.
    /// </summary>
    /// <param name="command">Причина и критерии отбора.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Сколько наблюдений отозвано.</returns>
    /// <exception cref="ObservationRequestException">
    /// 401 — нет пользователя, 422 — нет причины, нет фильтров, период перевёрнут или выборка
    /// больше <see cref="MaxBatchSize"/>.
    /// </exception>
    public async Task<int> HandleAsync(BulkRetractCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.UserId is not { } userId)
        {
            throw new ObservationRequestException(
                401,
                "authorization",
                "Войдите, чтобы отозвать наблюдения");
        }

        var reason = RetractObservationHandler.ValidateReason(command.Reason);

        var filter = new BulkRetractFilter(
            command.ReporterId,
            command.SourceId,
            command.From,
            command.To);

        if (!filter.HasAny)
        {
            throw new ObservationRequestException(
                422,
                "filters",
                "Нужен хотя бы один фильтр: reporterId, sourceId или период");
        }

        if (command.From is { } from && command.To is { } to && from > to)
        {
            throw new ObservationRequestException(
                422,
                "to",
                "Начало периода позже его конца");
        }

        var now = time.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        try
        {
            var candidates = await repository.CountRetractableAsync(filter, ct);
            if (candidates > MaxBatchSize)
            {
                throw new ObservationRequestException(
                    422,
                    "filters",
                    $"Под фильтр попадает больше {MaxBatchSize} наблюдений: сузьте фильтр");
            }

            var rows = candidates == 0
                ? Array.Empty<RetractedObservationRow>()
                : (IReadOnlyList<RetractedObservationRow>)await repository.RetractBulkAsync(
                    filter, userId, reason, now, ct);

            var criteria = new
            {
                reporterId = command.ReporterId,
                sourceId = command.SourceId,
                from = command.From,
                to = command.To,
            };

            // Одна запись на операцию: критерии, количество и причина.
            await audit.WriteAsync(
                "observation.retract",
                "observation",
                entityId: null,
                before: new { criteria, isRetracted = false },
                after: new { criteria, retractedCount = rows.Count },
                reason: reason,
                ct: ct);

            await db.SaveChangesAsync(ct);

            foreach (var mapObjectId in rows.Select(row => row.MapObjectId).Distinct())
            {
                await recalculator.RecalculateAsync(mapObjectId, now, ct);
            }

            await transaction.CommitAsync(ct);

            return rows.Count;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);

            throw;
        }
    }
}
