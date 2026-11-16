using Microsoft.Extensions.DependencyInjection;

using Sources.Contracts;

namespace Ingestion.Llm.OpenAi;

/// <summary>
/// Источник настроек LLM для кэша <see cref="OpenAiOptionsMonitor"/>.
/// </summary>
/// <remarks>
/// Выделен отдельным контрактом, потому что <c>ISecretReader</c> scoped (он работает поверх
/// <c>MayakDbContext</c>), а кэш настроек — singleton: singleton не имеет права захватывать
/// scoped-зависимость. Реализация по умолчанию (<see cref="ScopedLlmSettingsSource"/>) создаёт
/// новый scope на каждое чтение секрета.
/// </remarks>
public interface ILlmSettingsSource
{
    /// <summary>Читает текущие настройки LLM; <c>null</c> — секрет ещё не задан.</summary>
    Task<LlmSettings?> ReadAsync(CancellationToken ct);
}

/// <summary>
/// Singleton-обёртка над scoped <see cref="ISecretReader"/>: каждое чтение идёт в новом scope,
/// поэтому <c>DbContext</c> живёт ровно один запрос настроек и не удерживается кэшем.
/// </summary>
internal sealed class ScopedLlmSettingsSource(IServiceScopeFactory scopeFactory) : ILlmSettingsSource
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    public async Task<LlmSettings?> ReadAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<ISecretReader>();

        return await reader.ReadLlmAsync(ct).ConfigureAwait(false);
    }
}
