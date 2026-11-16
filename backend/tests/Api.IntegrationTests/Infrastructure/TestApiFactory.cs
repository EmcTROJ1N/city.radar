using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Api.IntegrationTests.Infrastructure;

/// <summary>
/// Хост Api на БД из <see cref="PostgresFixture"/>: окружение <c>Testing</c> (включает сидеры
/// dev-пользователей) и фиксированный ключ подписи JWT вместо ключа из конфигурации.
/// </summary>
/// <remarks>
/// Rate limit по умолчанию выключен: большинство тестов проверяют бизнес-правила и создают
/// много отметок одним пользователем. Тесты самого лимитера передают
/// <c>RateLimiting:Enabled=true</c> и нужные лимиты через <paramref name="settings"/>
/// (см. <c>Map/CacheAndRateLimitTests</c> и <c>Rate_limit_exceeded_429</c>).
/// </remarks>
internal sealed class TestApiFactory(
    string connectionString,
    Action<IServiceCollection>? configureServices = null,
    IReadOnlyDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    public const string SigningKey = "mayak-integration-tests-signing-key-0123456789";

    /// <summary>
    /// Пароль dev-входа для окружения <c>Testing</c>: <c>appsettings.Development.json</c> там не
    /// читается, поэтому включаем эндпоинт явно (тесты могут переопределить значение через
    /// <c>settings</c>).
    /// </summary>
    public const string DevAuthPassword = "mayak-tests-dev-auth";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Mayak", connectionString);
        builder.UseSetting("Jwt:Issuer", "mayak-tests");
        builder.UseSetting("Jwt:Audience", "mayak-tests");
        builder.UseSetting("Jwt:SigningKey", SigningKey);
        builder.UseSetting("DevAuth:Password", DevAuthPassword);
        // Фоновый пересчёт по TTL в тестах запускается вручную (SweepOnceAsync).
        builder.UseSetting("Observations:SweeperEnabled", "false");
        // Фоновый диспетчер outbox «api» в тестах запускается вручную через DispatchOnceAsync.
        builder.UseSetting("Outbox:ApiDispatcherEnabled", "false");
        // Лимиты включаются точечно в тестах rate limit.
        builder.UseSetting("RateLimiting:Enabled", "false");
        // Проверка секретов ходит в реальные Telegram/LLM/прокси; в тестах она выключена.
        builder.UseSetting("Secrets:ValidateOnSave", "false");

        if (settings is not null)
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        }

        if (configureServices is not null)
        {
            builder.ConfigureTestServices(configureServices);
        }
    }
}
