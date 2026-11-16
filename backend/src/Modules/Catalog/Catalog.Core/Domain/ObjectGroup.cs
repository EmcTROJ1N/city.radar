namespace Catalog.Core.Domain;

/// <summary>
/// Группа объектов (таблица <c>object_groups</c>) — необязательная группировка фиксаций для
/// фильтра карты. Имя уникально без учёта регистра (индекс по <c>lower(name)</c>);
/// удаление группы не запрещено: у будущих фиксаций <c>group_id</c> станет <c>null</c>.
/// </summary>
internal sealed class ObjectGroup
{
    public Guid Id { get; set; }

    public Guid? CategoryId { get; set; }

    public string Name { get; set; } = null!;

    public string? Color { get; set; }

    public bool IsVisibleByDefault { get; set; }
}
