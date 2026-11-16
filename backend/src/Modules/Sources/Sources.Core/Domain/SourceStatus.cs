namespace Sources.Core.Domain;

/// <summary>
/// Статус, который «докладывает» источник (таблица <c>source_statuses</c>;
///): код, название, описание для LLM, цвет из палитры, иконка и порядок показа.
/// Статусы принадлежат источнику — у разных источников независимые списки, поэтому код
/// уникален только внутри источника. Может быть создан из записи справочника
/// <c>status_definitions</c> (<see cref="StatusDefinitionId"/>) или как собственный статус.
/// </summary>
internal sealed class SourceStatus
{
    public Guid Id { get; set; }

    /// <summary>Источник-владелец; запись удаляется каскадом вместе с источником.</summary>
    public Guid SourceId { get; set; }

    /// <summary>Запись библиотеки стилей, из которой создан статус; <c>null</c> — собственный статус.</summary>
    public Guid? StatusDefinitionId { get; set; }

    /// <summary>Код, уникальный внутри источника (<c>unique (source_id, code)</c>).</summary>
    public string Code { get; set; } = null!;

    /// <summary>Название статуса.</summary>
    public string Name { get; set; } = null!;

    /// <summary>Подсказка для LLM: что означает статус.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Ключ палитры оформления: <c>ok|limited|none|queue|schedule|stale</c>.</summary>
    public string Color { get; set; } = null!;

    /// <summary>Ключ иконки из темы.</summary>
    public string Icon { get; set; } = null!;

    /// <summary>Порядок выдачи в списках; меньшее значение — выше.</summary>
    public int SortOrder { get; set; }
}
