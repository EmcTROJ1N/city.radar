using BuildingBlocks.Messaging;
using BuildingBlocks.Persistence;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Moderation.Contracts;
using Moderation.Core.Anomaly;
using Moderation.Core.Handlers;
using Moderation.Core.Messaging;
using Moderation.Core.Persistence;
using Moderation.Core.Services;

namespace Moderation.Core;

/// <summary>Регистрация модуля модерации в composition root.</summary>
public static class ModerationServiceCollectionExtensions
{
    /// <summary>
    /// Добавляет модель <c>moderation_cases</c> в единый <see cref="MayakDbContext"/>, чтение
    /// очереди, создание кейсов, обработчик outbox <c>ModerationCaseRequested</c> и детектор
    /// аномалий по событию <c>ObservationCreated</c>. Требует, чтобы
    /// <see cref="PersistenceServiceCollectionExtensions.AddPersistence"/> уже был вызван.
    /// </summary>
    /// <remarks>
    /// Фоновый <c>OutboxDispatcher</c> здесь не регистрируется: его destination выбирает хост.
    /// В Api диспетчер <c>api</c> подключается отдельной задачей — до этого обработчик
    /// доступен тестам и ручному запуску без фонового цикла.
    /// </remarks>
    public static IServiceCollection AddModeration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IModelContributor, ModerationModelContributor>();
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<ModerationCaseService>();
        services.AddScoped<ModerationCaseCreator>();
        services.AddScoped<IModerationCaseCreator>(
            provider => provider.GetRequiredService<ModerationCaseCreator>());
        services.AddScoped<ApproveCaseHandler>();
        services.AddScoped<RejectCaseHandler>();

        // Детектор аномалий: обработчик события ObservationCreated читает настройки
        // на каждой проверке, поэтому их смена действует без перезапуска. Зависимости
        // IGlobalSettings/IMapObjectDirectory берутся через GetService: минимальные контейнеры
        // (например, тест диспетчера только с AddModeration) не обязаны регистрировать каталог
        // и IdentityAccess. В Api оба сервиса присутствуют всегда.
        services.AddScoped(provider => new AnomalyDetector(
            provider.GetRequiredService<MayakDbContext>(),
            provider.GetRequiredService<IModerationCaseCreator>(),
            provider.GetService<IdentityAccess.Contracts.IGlobalSettings>(),
            provider.GetService<Catalog.Contracts.IMapObjectDirectory>()));

        services.AddOutboxHandler<ModerationCaseRequestedHandler>();
        services.AddOutboxHandler<AnomalyHandler>();

        return services;
    }
}
