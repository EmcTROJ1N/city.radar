using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Sources.Contracts;
using Sources.Core.Secrets;

namespace Sources.Core;

/// <summary>
/// Регистрация доступа Worker-а к секретам. Вызывается только
/// из composition root процесса обработки: в <c>Api.Host</c> ни <see cref="ISecretReader"/>,
/// ни <see cref="ISourceConnectionResolver"/> не регистрируются, поэтому API не может
/// прочитать значение секрета. Исключение — <see cref="SecretValidationService"/>:
/// он расшифровывает только параметры прокси для проверки доступности при сохранении и
/// никогда не возвращает их наружу.
/// </summary>
public static class SecretsServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует <see cref="ISecretReader"/> и <see cref="ISourceConnectionResolver"/>.
    /// Требует зарегистрированных <c>MayakDbContext</c> (model contributors модуля источников),
    /// <c>ISecretCipher</c> и <c>IGlobalSettings</c> (глобальный прокси).
    /// </summary>
    public static IServiceCollection AddSecretReader(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<SecretReader>();
        services.TryAddScoped<ISecretReader>(provider => provider.GetRequiredService<SecretReader>());
        services.TryAddScoped<ISourceConnectionResolver, SourceConnectionResolver>();

        return services;
    }
}
