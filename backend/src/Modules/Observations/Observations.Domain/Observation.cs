namespace Observations.Domain;

/// <summary>
/// Наблюдение за состоянием фиксации (таблица <c>observations</c>). Сущность
/// неизменяемая: допустим только отзыв, поэтому все поля доступны только на чтение.
/// Таймаут актуальности фиксируется в <see cref="ExpiresAt"/> при создании и позже
/// не пересчитывается.
/// </summary>
public sealed class Observation
{
    /// <summary>Допустимое расхождение часов клиента и сервера: время наблюдения в будущем.</summary>
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);

    private Observation(
        Guid id,
        ObservationDraft draft,
        DateTimeOffset expiresAt,
        bool isRetracted)
    {
        Id = id;
        MapObjectId = draft.MapObjectId;
        SourceId = draft.SourceId;
        SourceStatusId = draft.SourceStatusId;
        Kind = draft.Kind;
        ReporterId = draft.ReporterId;
        ObservedAt = draft.ObservedAt;
        ReceivedAt = draft.ReceivedAt;
        ExpiresAt = expiresAt;
        Confidence = draft.Confidence;
        RawPayloadRef = draft.RawPayloadRef;
        Comment = draft.Comment;
        IsRetracted = isRetracted;
    }

    /// <summary>Идентификатор наблюдения.</summary>
    public Guid Id { get; }

    /// <summary>Фиксация, к которой относится наблюдение.</summary>
    public Guid MapObjectId { get; }

    /// <summary>Источник, по которому сделано наблюдение.</summary>
    public Guid SourceId { get; }

    /// <summary>Статус из списка этого источника.</summary>
    public Guid SourceStatusId { get; }

    /// <summary>Тип источника наблюдения.</summary>
    public SourceKind Kind { get; }

    /// <summary>Пользователь для ручной отметки или отметки модератора; иначе <c>null</c>.</summary>
    public Guid? ReporterId { get; }

    /// <summary>Время наблюдения по данным источника.</summary>
    public DateTimeOffset ObservedAt { get; }

    /// <summary>Время получения наблюдения системой.</summary>
    public DateTimeOffset ReceivedAt { get; }

    /// <summary>Момент истечения актуальности: <see cref="ObservedAt"/> + таймаут источника.</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>Достоверность 0..1; на выбор статуса не влияет.</summary>
    public float? Confidence { get; }

    /// <summary>Ссылка на исходное сообщение или иные исходные данные.</summary>
    public string? RawPayloadRef { get; }

    /// <summary>Необязательный текст ручной отметки.</summary>
    public string? Comment { get; }

    /// <summary>Отозвано ли наблюдение модератором.</summary>
    public bool IsRetracted { get; }

    /// <summary>
    /// Создаёт наблюдение и вычисляет <see cref="ExpiresAt"/> = <see cref="ObservedAt"/> + таймаут
    /// источника. Таймаут должен быть положительным, а время наблюдения — не более чем на пять
    /// минут впереди серверного (допуск на расхождение часов).
    /// </summary>
    /// <param name="draft">Данные нового наблюдения.</param>
    /// <param name="sourceTimeout">Таймаут актуальности источника.</param>
    /// <param name="now">Текущее серверное время.</param>
    /// <param name="id">
    /// Идентификатор наблюдения. Задаётся только обработчиком outbox для идемпотентности
    /// (детерминированный Guid из сообщения и фиксации); по умолчанию — новый случайный.
    /// </param>
    /// <param name="allowExpired">
    /// Разрешить создание наблюдения, таймаут которого уже истёк (<c>ObservedAt + timeout &lt;= now</c>).
    /// Нужно для одобрения старых сообщений модератором и приёма предложений Worker-а: такое
    /// наблюдение сохраняется для истории, но <see cref="StatusResolver"/> его не учитывает.
    /// Для ручных отметок остаётся <c>false</c>: пользователь не может выставить заведомо
    /// протухший статус.
    /// </param>
    /// <returns>Новое неизменяемое наблюдение.</returns>
    /// <exception cref="DomainException">
    /// Таймаут не положителен, время наблюдения слишком далеко в будущем или наблюдение уже
    /// протухло, а <paramref name="allowExpired"/> не задан.
    /// </exception>
    public static Observation Create(
        ObservationDraft draft,
        TimeSpan sourceTimeout,
        DateTimeOffset now,
        Guid? id = null,
        bool allowExpired = false)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (sourceTimeout <= TimeSpan.Zero)
        {
            throw new DomainException("Таймаут актуальности источника должен быть положительным.");
        }

        if (draft.ObservedAt - now > MaxFutureSkew)
        {
            throw new DomainException(
                "Время наблюдения не может быть более чем на пять минут впереди серверного.");
        }

        var expiresAt = draft.ObservedAt + sourceTimeout;
        if (!allowExpired && expiresAt <= now)
        {
            throw new DomainException(
                "Срок актуальности наблюдения уже истёк: выставить его нельзя.");
        }

        return new Observation(id ?? Guid.NewGuid(), draft, expiresAt, isRetracted: false);
    }

    /// <summary>
    /// Восстанавливает наблюдение из хранилища (гидратация записи БД) без повторной проверки
    /// правил создания: <see cref="ExpiresAt"/> уже зафиксирован при создании наблюдения и не
    /// пересчитывается, а <see cref="IsRetracted"/> отражает текущее состояние отзыва.
    /// Метод нужен инфраструктурному слою: конструктор домена закрыт, чтобы снаружи нельзя было
    /// получить наблюдение с произвольными вычисляемыми полями.
    /// </summary>
    /// <param name="id">Идентификатор наблюдения из БД.</param>
    /// <param name="draft">Сохранённые поля наблюдения.</param>
    /// <param name="expiresAt">Зафиксированный в БД момент истечения актуальности.</param>
    /// <param name="isRetracted">Отозвано ли наблюдение.</param>
    /// <returns>Наблюдение в сохранённом состоянии.</returns>
    public static Observation Restore(
        Guid id,
        ObservationDraft draft,
        DateTimeOffset expiresAt,
        bool isRetracted)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return new Observation(id, draft, expiresAt, isRetracted);
    }
}
