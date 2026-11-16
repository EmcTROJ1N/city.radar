using StackExchange.Redis;

using Testcontainers.Redis;

namespace Api.IntegrationTests.Infrastructure;

/// <summary>
/// Один контейнер Redis на тестовый класс: кэш карты и rate limit проверяются на
/// том же образе <c>redis:7-alpine</c>, что и в <c>deploy/docker-compose.yml</c>.
/// </summary>
/// <remarks>
/// Тесты лимитов идут последовательно внутри класса («postgres» collection), но ключи живут в
/// общем Redis, поэтому <see cref="FlushAsync"/> вызывается перед каждым тестом — счётчики
/// предыдущего теста не влияют на следующий.
/// </remarks>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7-alpine").Build();

    /// <summary>Строка подключения для <c>ConnectionStrings:Redis</c>.</summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <summary>Адрес заведомо недоступного Redis: проверка деградации.</summary>
    public static string UnavailableConnectionString { get; } = "127.0.0.1:1";

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Очищает базу Redis: изоляция счётчиков и кэша между тестами.</summary>
    public async Task FlushAsync()
    {
        var options = ConfigurationOptions.Parse(ConnectionString);
        options.AllowAdmin = true;

        await using var connection = await ConnectionMultiplexer.ConnectAsync(options);
        await connection.GetDatabase().ExecuteAsync("FLUSHDB");
    }
}
