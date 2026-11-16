namespace Ingestion.Abstractions;

/// <summary>
/// Нормализованное сообщение источника: pipeline разбора не зависит от мессенджера
/// и его формата — адаптер приводит сообщение к этому виду.
/// </summary>
/// <param name="SourceId">Идентификатор источника, из которого получено сообщение.</param>
/// <param name="ExternalId">Идентификатор сообщения во внешней системе; вместе с <paramref name="SourceId"/> уникален.</param>
/// <param name="Text">Текст сообщения.</param>
/// <param name="Author">Автор сообщения; <c>null</c>, если внешняя система его не отдаёт.</param>
/// <param name="Chat">Чат (название или идентификатор); <c>null</c>, если он неизвестен.</param>
/// <param name="Link">Ссылка на сообщение; <c>null</c>, если она недоступна.</param>
/// <param name="PostedAt">Время публикации сообщения.</param>
public sealed record RawMessage(
    Guid SourceId,
    string ExternalId,
    string Text,
    string? Author,
    string? Chat,
    string? Link,
    DateTimeOffset PostedAt);
