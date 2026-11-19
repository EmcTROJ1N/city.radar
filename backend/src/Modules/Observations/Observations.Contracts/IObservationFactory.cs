namespace Observations.Contracts;

/// <summary>
/// Создание наблюдения по решению модератора.
/// Контракт нужен модулю модерации: он не имеет права ссылаться на
/// <c>Observations.Application</c>/<c>Infrastructure</c>, но обязан создать
/// наблюдение и пересчитать проекцию <c>current_statuses</c> в той же транзакции, в которой
/// закрывается кейс и пишется аудит.
/// </summary>
public interface IObservationFactory
{
    /// <summary>
    /// Добавляет наблюдение и пересчитывает проекцию фиксации.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ModeratedObservation.ObservedAt"/> — время исходного сообщения
    /// (<c>raw_messages.posted_at</c>), а не текущее серверное время: одобрение старого
    /// сообщения не должно выдавать его за свежее.
    /// </para>
    /// <para>
    /// <c>ExpiresAt</c> считается как <see cref="ModeratedObservation.ObservedAt"/> + таймаут
    /// источника и может оказаться в прошлом. Такое наблюдение сохраняется для истории, но не
    /// влияет на текущий статус (проекция остаётся <c>stale</c>).
    /// </para>
    /// <para>
    /// Метод не коммитит транзакцию: вызывающий модерационный обработчик держит одну
    /// транзакцию с аудитом и сменой состояния кейса.
    /// </para>
    /// </remarks>
    /// <param name="m">Данные наблюдения, подтверждённого модератором.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Идентификатор созданного наблюдения.</returns>
    Task<Guid> CreateFromModerationAsync(ModeratedObservation m, CancellationToken ct);
}

/// <summary>
/// Наблюдение, которое модератор принял по кейсу.
/// </summary>
/// <param name="MapObjectId">Фиксация, выбранная модератором или предложенная системой.</param>
/// <param name="SourceId">Источник исходного сообщения.</param>
/// <param name="SourceStatusId">Статус из списка этого источника (предложенный или исправленный).</param>
/// <param name="RawMessageId">Исходное сообщение <c>raw_messages.id</c>.</param>
/// <param name="ObservedAt">Время сообщения <c>raw_messages.posted_at</c>.</param>
/// <param name="Kind">Тип источника: <see cref="SourceKind.Telegram"/> или <see cref="SourceKind.Max"/>.</param>
/// <param name="Confidence">Уверенность LLM из предложения системы; информационный показатель.</param>
public sealed record ModeratedObservation(
    Guid MapObjectId,
    Guid SourceId,
    Guid SourceStatusId,
    Guid RawMessageId,
    DateTimeOffset ObservedAt,
    SourceKind Kind,
    float? Confidence,
    string? Address = null);

/// <summary>
/// Тип источника наблюдения в контракте модуля наблюдений.
/// </summary>
/// <remarks>
/// Значения совпадают с <c>Observations.Domain.SourceKind</c>; отдельный enum нужен, чтобы
/// <c>Observations.Contracts</c> не зависел от домена (Architecture.Tests: Contracts не
/// ссылаются на Domain/Core). Перевод выполняется в реализации <see cref="IObservationFactory"/>.
/// </remarks>
public enum SourceKind
{
    /// <summary>Ручная отметка пользователя.</summary>
    Manual,

    /// <summary>Отметка модератора.</summary>
    Moderator,

    /// <summary>Наблюдение из Telegram-чата.</summary>
    Telegram,

    /// <summary>Наблюдение из MAX-чата.</summary>
    Max,
}
