using Microsoft.AspNetCore.Routing;

namespace IdentityAccess.Core.Admin;

/// <summary>
/// Подключение админских эндпоинтов Identity &amp; Access: пользователи и роли,
/// журнал аудита, глобальные настройки. Вызывается внутри префикса <c>/api/v1</c> из
/// <c>Api.Host/Program.cs</c>.
/// </summary>
public static class AdminEndpoints
{
    /// <summary>Регистрирует <c>/admin/users</c>, <c>/admin/audit</c> и <c>/admin/settings</c>.</summary>
    public static IEndpointRouteBuilder MapIdentityAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapUsersEndpoints();
        endpoints.MapAuditEndpoints();
        endpoints.MapSettingsEndpoints();

        return endpoints;
    }
}
