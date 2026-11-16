using System.Text.Json;

using BuildingBlocks.Persistence;

using Moderation.Contracts;
using Moderation.Core.Domain;

namespace Moderation.Core.Services;

/// <summary>
/// Реализация <see cref="IModerationCaseCreator"/>: добавляет кейс в текущий
/// <see cref="MayakDbContext"/> и не сохраняет его.
/// </summary>
/// <remarks>
/// <c>SaveChanges</c> вызывается вызывающим кодом: так кейс попадает в ту же транзакцию, что и
/// бизнес-изменение (жалоба, обработчик outbox), и откатывается вместе с
/// ней. Валидация дублирует check-ограничения <c>moderation_cases</c>.
/// </remarks>
internal sealed class ModerationCaseCreator(MayakDbContext db, TimeProvider timeProvider)
    : IModerationCaseCreator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public Task<Guid> CreateAsync(NewModerationCase c, CancellationToken ct) =>
        CreateAsync(c, outboxMessageId: null, ct);

    /// <summary>
    /// Добавляет кейс с ключом идемпотентности outbox-сообщения (используется обработчиком
    /// <c>ModerationCaseRequested</c>). Также не вызывает <c>SaveChanges</c>.
    /// </summary>
    internal Task<Guid> CreateAsync(NewModerationCase c, Guid? outboxMessageId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        ct.ThrowIfCancellationRequested();

        Validate(c);

        var moderationCase = new ModerationCase
        {
            Id = Guid.NewGuid(),
            Kind = c.Kind,
            State = ModerationCaseStates.Open,
            SourceId = c.SourceId,
            RawMessageId = c.RawMessageId,
            ObservationId = c.ObservationId,
            MapObjectId = c.MapObjectId,
            ReporterId = c.ReporterId,
            Comment = c.Comment,
            Proposed = Serialize(c.Proposed),
            CreatedAt = timeProvider.GetUtcNow(),
            OutboxMessageId = outboxMessageId,
        };

        db.Set<ModerationCase>().Add(moderationCase);

        // SaveChanges не вызывается: кейс уходит в БД вместе с изменением вызывающего кода.
        return Task.FromResult(moderationCase.Id);
    }

    /// <summary>
    /// Проверки, которые в БД продублированы check-ограничениями: известный вид кейса и
    /// «complaint ⇔ reporter_id».
    /// </summary>
    private static void Validate(NewModerationCase c)
    {
        if (!ModerationCaseKinds.IsKnown(c.Kind))
        {
            throw new ArgumentException($"Неизвестный вид кейса: {c.Kind}", nameof(c));
        }

        if (c.Kind == ModerationCaseKinds.Complaint && c.ReporterId is null)
        {
            throw new ArgumentException(
                "Кейс complaint требует автора жалобы (reporterId).",
                nameof(c));
        }

        if (c.Kind != ModerationCaseKinds.Complaint && c.ReporterId is not null)
        {
            throw new ArgumentException(
                "reporterId допустим только для кейса complaint.",
                nameof(c));
        }
    }

    private static string? Serialize(object? proposed) =>
        proposed is null
            ? null
            : JsonSerializer.Serialize(proposed, proposed.GetType(), JsonOptions);
}
