using Ingestion.Abstractions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Sources.Contracts;

namespace Ingestion.Llm.OpenAi;

/// <summary>
/// Регистрация OpenAI-совместимого провайдера LLM. Вызывается из composition root
/// Worker-а: в Api провайдер не нужен и ключ LLM там недоступен.
/// </summary>
public static class OpenAiServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует <see cref="OpenAiOptionsMonitor"/> и <see cref="ILlmProvider"/>.
    /// Требует зарегистрированного <see cref="ISecretReader"/> (см. <c>AddSecretReader()</c>).
    /// </summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="cacheTtl">TTL кэша настроек; <c>null</c> — 30 секунд.</param>
    public static IServiceCollection AddOpenAiProvider(
        this IServiceCollection services,
        TimeSpan? cacheTtl = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        // Кэш настроек — singleton, а ISecretReader — scoped: читаем секрет через новый scope,
        // чтобы singleton не захватывал DbContext (и не ломал ValidateScopes в Development).
        services.AddSingleton<ILlmSettingsSource, ScopedLlmSettingsSource>();

        services.AddSingleton(provider => new OpenAiOptionsMonitor(
            provider.GetRequiredService<ILlmSettingsSource>(),
            provider.GetRequiredService<TimeProvider>(),
            cacheTtl ?? OpenAiOptionsMonitor.DefaultCacheTtl));

        services.AddSingleton<ILlmProvider>(provider => new OpenAiProvider(
            provider.GetRequiredService<OpenAiOptionsMonitor>(),
            provider.GetService<ILogger<OpenAiProvider>>() ?? NullLogger<OpenAiProvider>.Instance));

        return services;
    }
}
