namespace Catalog.Core.Domain;

/// <summary>
/// Категория каталога (таблица <c>categories</c>): группирует типы объектов и записи
/// справочника статусов. <see cref="Code"/> уникален, <see cref="Name"/> — непустой текст.
/// </summary>
internal sealed class Category
{
    public Guid Id { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string? Icon { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; }
}
