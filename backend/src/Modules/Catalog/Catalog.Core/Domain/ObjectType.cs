namespace Catalog.Core.Domain;

/// <summary>
/// Тип объекта каталога (таблица <c>object_types</c>): код, категория и допустимый вид
/// геометрии (<c>point</c>, <c>polygon</c>, <c>any</c>).
/// </summary>
internal sealed class ObjectType
{
    public Guid Id { get; set; }

    public Guid CategoryId { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string GeometryKind { get; set; } = "any";
}
