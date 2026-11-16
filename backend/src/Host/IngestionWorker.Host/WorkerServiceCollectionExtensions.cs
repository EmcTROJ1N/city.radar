using Ingestion.Abstractions;
using Ingestion.Adapters.TelegramBotApi;
using Ingestion.Adapters.TelegramMtProto;
using Ingestion.Pipeline;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IngestionWorker.Host;

/// <summary>
/// Регистрация сервисов Worker-а. Вызывается только из composition root
/// <c>IngestionWorker.Host</c> и из тестов Worker-а: HTTP-эндпоинтов здесь нет.
/// </summary>
public static class WorkerServiceCollectionExtensions
{
    /// <summary>
    /// Добавляет чтение конфигурации источников, supervisor адаптеров, обработку сообщений
    /// и фоновый опрос настроек. Требует зарегистрированных <c>MayakDbContext</c>,
    /// <c>IOutbox</c>, контрактов Sources/Catalog и <see cref="Microsoft.Extensions.Logging.ILogger"/>.
    /// </summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="options">Настройки опроса и перезапуска.</param>
    public static IServiceCollection AddIngestionWorker(
        this IServiceCollection services,
        WorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        // Прокси: разрешение цепочки «источник → аккаунт → глобальный».
        services.AddSingleton<ProxyResolver>();

        // Адаптеры Telegram: Bot API и MTProto делят тип telegram_chat и различаются
        // режимом источника (settings.mode = bot | userbot). Порядок регистрации — порядок выбора.
        services.AddSingleton<IMtProtoSessionStore>(NullMtProtoSessionStore.Instance);
        services.AddSingleton<ITelegramClientTransportFactory>(provider =>
            new WTelegramClientTransportFactory(
                provider.GetRequiredService<IMtProtoSessionStore>(),
                provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ISourceAdapterFactory, TelegramBotApiAdapterFactory>();
        services.AddSingleton<ISourceAdapterFactory, TelegramMtProtoAdapterFactory>();

        services.AddScoped<SourceConfigReader>();
        services.AddScoped<RawMessageStore>();
        services.AddScoped<SourceSnapshotBuilder>();
        services.AddScoped<MessagePipeline>();
        services.AddScoped<MessageIngestionService>();

        services.AddSingleton<SourceSupervisor>();
        services.AddSingleton<SourceConfigWatcher>();
        services.AddHostedService(provider => provider.GetRequiredService<SourceConfigWatcher>());

        return services;
    }
}
