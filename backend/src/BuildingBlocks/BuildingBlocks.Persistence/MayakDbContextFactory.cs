using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BuildingBlocks.Persistence;

/// <summary>
/// Контекст для design-time команд EF (`dotnet ef migrations add`, `dotnet ef database update`).
/// Строка подключения берётся из переменной окружения MAYAK_DB.
/// </summary>
public sealed class MayakDbContextFactory : IDesignTimeDbContextFactory<MayakDbContext>
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=mayak;Username=mayak;Password=mayak";

    public MayakDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("MAYAK_DB");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = DefaultConnectionString;
        }

        var options = new DbContextOptionsBuilder<MayakDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql
                .UseNetTopologySuite()
                .MigrationsAssembly(typeof(MayakDbContext).Assembly.GetName().Name!))
            .UseSnakeCaseNamingConvention()
            // Модульные миграции (Identity) написаны вручную и не обновляют ModelSnapshot;
            // предупреждение EF о «pending model changes» здесь не несёт смысла.
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        return new MayakDbContext(options, []);
    }
}
