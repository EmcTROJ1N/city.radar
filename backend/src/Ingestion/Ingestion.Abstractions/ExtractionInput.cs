namespace Ingestion.Abstractions;

/// <summary>
/// Входные данные разбора сообщения: всё, что нужно экстрактору, чтобы построить
/// системный промпт и проверить ответ LLM.
/// </summary>
/// <param name="Text">Текст сообщения источника.</param>
/// <param name="Statuses">Статусы источника; LLM может вернуть только код из этого списка.</param>
/// <param name="UserPrompt">Промпт-фильтр пользователя из настроек источника.</param>
/// <param name="Objects">
/// Фиксации источника для режима «определять фиксацию по тексту»; <c>null</c>, если режим
/// выключен или у источника одна фиксация. Краткие карточки повторяют данные каталога,
/// не создавая зависимости от <c>Catalog.Contracts</c>.
/// </param>
/// <param name="LinkByText">Включён ли режим определения фиксации по тексту.</param>
/// <param name="ConfidenceThreshold">Порог confidence: ниже него предложение уходит в модерацию.</param>
public sealed record ExtractionInput(
    string Text,
    IReadOnlyList<ExtractionStatus> Statuses,
    string UserPrompt,
    IReadOnlyList<ExtractionObject>? Objects,
    bool LinkByText,
    double ConfidenceThreshold);

/// <summary>Статус источника, допустимый в ответе LLM.</summary>
/// <param name="Code">Код статуса, уникальный в пределах источника.</param>
/// <param name="Name">Название статуса для человека.</param>
/// <param name="Description">Описание для LLM: что означает статус.</param>
public sealed record ExtractionStatus(string Code, string Name, string Description);

/// <summary>Краткая карточка фиксации источника для выбора её моделью.</summary>
/// <param name="Id">Идентификатор фиксации.</param>
/// <param name="Name">Название фиксации.</param>
/// <param name="Address">Адрес; <c>null</c>, если не задан.</param>
public sealed record ExtractionObject(Guid Id, string Name, string? Address);
