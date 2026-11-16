namespace Moderation.Contracts;

/// <summary>
/// Создание кейса очереди модерации. Контракт нужен другим
/// модулям (например, жалобам пользователей) и обработчику outbox
/// <c>ModerationCaseRequested</c>.
/// </summary>
public interface IModerationCaseCreator
{
    /// <summary>
    /// Добавляет кейс в текущий <c>MayakDbContext</c> и возвращает его идентификатор.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Метод <b>не вызывает</b> <c>SaveChanges</c>: кейс сохраняется тем же
    /// <c>SaveChanges</c>/транзакцией, что и бизнес-изменение вызывающего кода, и откатывается
    /// вместе с ним. Вызывающий обязан сохранить изменения сам.
    /// </para>
    /// <para>
    /// Неизвестный <see cref="NewModerationCase.Kind"/>, а также <c>complaint</c> без
    /// <see cref="NewModerationCase.ReporterId"/> (или <c>reporterId</c> у кейса другого вида)
    /// отклоняются <see cref="ArgumentException"/> — те же правила продублированы
    /// check-ограничениями <c>moderation_cases</c> в БД.
    /// </para>
    /// </remarks>
    /// <param name="c">Данные нового кейса.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Идентификатор добавленного кейса.</returns>
    Task<Guid> CreateAsync(NewModerationCase c, CancellationToken ct);
}

/// <summary>
/// Данные нового кейса модерации. Поля соответствуют <c>moderation_cases</c>;
/// <see cref="Proposed"/> сериализуется в <c>jsonb</c> (camelCase).
/// </summary>
/// <param name="Kind">
/// Вид кейса: <c>low_confidence</c>, <c>invalid_status</c>, <c>object_not_found</c>,
/// <c>invalid_response</c>, <c>anomaly</c> или <c>complaint</c>.
/// </param>
/// <param name="SourceId">Источник, сообщение которого попало в очередь; <c>null</c> для жалобы.</param>
/// <param name="RawMessageId">Исходное сообщение источника; <c>null</c> для ручных кейсов.</param>
/// <param name="MapObjectId">Фиксация, к которой отнесён кейс; <c>null</c>, если не определена.</param>
/// <param name="ObservationId">Связанное наблюдение; <c>null</c>, если его ещё нет.</param>
/// <param name="ReporterId">Автор жалобы; обязателен только для <c>complaint</c>.</param>
/// <param name="Comment">Текст жалобы; <c>null</c> для остальных видов.</param>
/// <param name="Proposed">
/// Предложение системы (статус, confidence, <c>evidenceQuote</c>, причина) — произвольный
/// объект, сериализуемый в <c>jsonb</c>.
/// </param>
public sealed record NewModerationCase(
    string Kind,
    Guid? SourceId,
    Guid? RawMessageId,
    Guid? MapObjectId,
    Guid? ObservationId,
    Guid? ReporterId,
    string? Comment,
    object? Proposed);
