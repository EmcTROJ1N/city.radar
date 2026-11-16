namespace Catalog.Core.Domain;

/// <summary>
/// Привязка источника к фиксации (таблица <c>map_object_sources</c>):
/// у фиксации может быть несколько источников, источник обслуживает несколько фиксаций.
/// </summary>
/// <remarks>
/// Внешний ключ на <c>sources(id)</c> (<c>map_object_sources_source_id_fkey</c>, on delete cascade)
/// создаётся миграцией <c>Sources</c>; навигации на источник в модели нет — он
/// принадлежит другому модулю.
/// </remarks>
internal sealed class MapObjectSource
{
    public Guid MapObjectId { get; set; }

    public Guid SourceId { get; set; }
}
