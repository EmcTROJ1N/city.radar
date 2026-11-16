using BuildingBlocks.Persistence;

using IdentityAccess.Contracts;
using IdentityAccess.Core.Auth;
using IdentityAccess.Core.Seeding;
using IdentityAccess.Core.Settings;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IdentityAccess.Core;

/// <summary>Регистрация модуля Identity &amp; Access в composition root.</summary>
public static class IdentityAccessServiceCollectionExtensions
{
    /// <summary>
    /// Добавляет модель модуля в единый <see cref="MayakDbContext"/>, сидеры ролей и
    /// dev-пользователей, их runner, а также <see cref="ICurrentUser"/> поверх HttpContext.
    /// Требует, чтобы <see cref="PersistenceServiceCollectionExtensions.AddPersistence"/> уже был вызван.
    /// </summary>
    public static IServiceCollection AddIdentityAccess(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IModelContributor, Persistence.IdentityModelContributor>();

        services.AddScoped<RoleSeeder>();
        services.AddScoped<DevSeeder>();
        // Runner сам создаёт scope и резолвится из корневого provider при старте приложения,
        // поэтому он transient (scoped из корня запрещён при ValidateScopes).
        services.AddTransient<IdentitySeedRunner>();

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

        // Аудит и настройки нужны и без JWT (например, фоновым сценариям): время — системное,
        // если AddIdentityAccessAuth не вызывался.
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IAuditLog, Audit.AuditLog>();
        services.AddScoped<GlobalSettingsService>();
        services.AddScoped<IGlobalSettings>(provider => provider.GetRequiredService<GlobalSettingsService>());

        return services;
    }

    /// <summary>
    /// Регистрирует сервисы JWT и refresh-токенов. Вызывается только когда настроена БД:
    /// без <see cref="JwtOptions"/> аутентификация и эндпоинты <c>/auth/*</c> не работают.
    /// </summary>
    public static IServiceCollection AddIdentityAccessAuth(
        this IServiceCollection services,
        JwtOptions jwtOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(jwtOptions);

        services.AddSingleton(jwtOptions);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<TokenService>();
        services.AddScoped<RefreshTokenService>();
        services.AddScoped<UserProfileService>();

        return services;
    }
}
