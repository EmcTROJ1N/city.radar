namespace Catalog.Core.Domain;

/// <summary>
/// Запись справочника статусов (таблица <c>status_definitions</c>) — библиотека стилей.
/// Цвет — ключ палитры оформления (<c>ok|limited|none|queue|schedule|stale</c>), а не
/// произвольный hex: контраст должен соблюдаться в обеих темах.
/// </summary>
internal sealed class StatusDefinition
{
    public Guid Id { get; set; }

    public Guid? CategoryId { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string Color { get; set; } = null!;

    public string Icon { get; set; } = null!;

    public int Severity { get; set; }

    public bool IsPositive { get; set; }
}
