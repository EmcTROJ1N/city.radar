using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BuildingBlocks.Security;

/// <summary>Регистрация шифрования секретов.</summary>
public static class SecretCipherServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует <see cref="ISecretCipher"/> и <see cref="MasterKeyProvider"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// В Production мастер-ключ обязан быть файлом/Docker secret: если
    /// <c>MAYAK_MASTER_KEY_FILE</c> не задан или файл некорректен, метод бросает
    /// <see cref="InvalidOperationException"/> прямо при вызове, и приложение не стартует.
    /// Конфигурация и случайный ключ в Production намеренно не используются.
    /// </para>
    /// <para>
    /// В Development/Testing порядок такой: файл из <c>MAYAK_MASTER_KEY_FILE</c>, если задан;
    /// иначе base64-ключ из <see cref="MasterKeyProvider.ConfigurationKey"/>; иначе случайный ключ
    /// на процесс. Со случайным ключом секреты не переживут перезапуск процесса.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Production без корректного мастер-ключа.
    /// </exception>
    public static IServiceCollection AddSecretCipher(
        this IServiceCollection services,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);

        if (environment.IsProduction())
        {
            // Проверяем ключ сразу, а не в фабрике: приложение без ключа не должно стартовать.
            var keys = MasterKeyProvider.FromEnvironment();
            services.TryAddSingleton(keys);
            services.TryAddSingleton<ISecretCipher>(new AesGcmSecretCipher(keys));
            return services;
        }

        // Development/Testing: файл, затем Security:MasterKey, затем случайный ключ на процесс.
        services.TryAddSingleton(serviceProvider =>
            MasterKeyProvider.FromEnvironmentOrGenerate(serviceProvider.GetService<IConfiguration>()));
        services.TryAddSingleton<ISecretCipher>(serviceProvider =>
            new AesGcmSecretCipher(serviceProvider.GetRequiredService<MasterKeyProvider>()));

        return services;
    }
}
