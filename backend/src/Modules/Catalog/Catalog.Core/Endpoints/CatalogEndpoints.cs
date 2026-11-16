using Microsoft.AspNetCore.Routing;

namespace Catalog.Core.Endpoints;

/// <summary>
/// Подключение эндпоинтов каталога: категории, типы объектов, группы и справочник
/// статусов. Вызывается внутри префикса <c>/api/v1</c> из <c>Api.Host/Program.cs</c>.
/// </summary>
public static class CatalogEndpoints
{
    /// <summary>Регистрирует <c>/catalog/categories</c>, <c>/catalog/object-types</c>,
    /// <c>/catalog/groups</c> и <c>/catalog/statuses</c>.</summary>
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapCategoriesEndpoints();
        endpoints.MapObjectTypesEndpoints();
        endpoints.MapGroupsEndpoints();
        endpoints.MapStatusesEndpoints();
        endpoints.MapObjectsEndpoints();

        return endpoints;
    }
}
