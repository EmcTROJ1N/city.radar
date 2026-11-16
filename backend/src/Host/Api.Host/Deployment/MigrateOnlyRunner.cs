using BuildingBlocks.Persistence;

using IdentityAccess.Core;
using IdentityAccess.Core.Seeding;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Host.Deployment;

/// <summary>
/// Режим «только миграции» для шага развёртывания: применяет EF Core-миграции
/// из <c>BuildingBlocks.Persistence</c> и актуализирует роли/права (<see cref="RoleSeeder"/>),
/// после чего процесс завершается. Dev-данные не сеются ни в каком окружении.
/// </summary>
/// <remarks>
/// <para>
/// Схему создаёт только этот режим/тесты; Api в Production миграции при старте не запускает
/// (см. <c>Program.cs</c>), поэтому деплой сначала выполняет <c>migrate</c>, затем поднимает Api.
/// </para>
/// <para>
/// Мастер-ключ секретов и ключ подписи JWT здесь не нужны: регистрируются только persistence и
/// IdentityAccess, поэтому шаг миграций можно запускать до выдачи секретов приложению.
/// </para>
/// </remarks>
public static class MigrateOnlyRunner
{
    /// <summary>Аргумент командной строки, включающий режим «только миграции».</summary>
    public const string Argument = "--migrate-only";

    private const string ConnectionStringName = "Mayak";

    /// <summary>Запрошен ли режим «только миграции».</summary>
    public static bool IsRequested(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return Array.Exists(args, argument =>
            string.Equals(argument, Argument, StringComparison.Ordinal));
    }

    /// <summary>
    /// Применяет миграции и роли. Возвращает код выхода процесса: <c>0</c> — успех,
    /// <c>1</c> — ошибка (нет строки подключения, недоступна БД, миграция упала).
    /// Повторный запуск идемпотентен.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        var builder = global::Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

        var connectionString = builder.Configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine(
                $"Не задана строка подключения ConnectionStrings:{ConnectionStringName}. " +
                "Задайте её переменной окружения ConnectionStrings__Mayak.");
            return 1;
        }

        // Минимальный composition root: модель БД для миграций не нужна (SQL пишется вручную),
        // поэтому contributors модулей не регистрируются.
        builder.Services.AddPersistence(connectionString);
        builder.Services.AddIdentityAccess();

        using var host = builder.Build();

        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MayakDbContext>();

            await db.Database.MigrateAsync(cancellationToken);
            await scope.ServiceProvider.GetRequiredService<RoleSeeder>().SeedAsync(cancellationToken);

            Console.WriteLine("Миграции применены, роли и права приведены к эталону.");
            return 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Код выхода 1 останавливает deploy: Api не должен стартовать на неполной схеме.
            Console.Error.WriteLine($"Не удалось применить миграции: {exception.Message}");
            return 1;
        }
    }
}
