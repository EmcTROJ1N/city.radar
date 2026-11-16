namespace BuildingBlocks.Messaging;

/// <summary>
/// Transactional outbox (решение): запись в таблицу <c>outbox_messages</c>
/// в текущем scoped <see cref="Persistence.MayakDbContext"/>.
/// </summary>
/// <remarks>
/// <see cref="Enqueue"/> намеренно не вызывает <c>SaveChanges</c>: событие должно сохраниться
/// в той же транзакции, что и бизнес-данные, — иначе возможна либо потеря события, либо
/// событие без изменения данных.
/// </remarks>
public interface IOutbox
{
    /// <summary>
    /// Добавляет сообщение к отправке. <paramref name="destination"/> — сторона-получатель
    /// (<c>api</c> или <c>worker</c>, см. check в <c>db/schema.sql</c>), <paramref name="type"/> —
    /// тип события для выбора <see cref="IOutboxHandler"/> (<c>ObservationProposed</c>,
    /// <c>ModerationCaseRequested</c> и т. п.). <paramref name="payload"/> сериализуется
    /// System.Text.Json с web-настройками (camelCase) в <c>jsonb</c>.
    /// </summary>
    void Enqueue(string destination, string type, object payload);
}
