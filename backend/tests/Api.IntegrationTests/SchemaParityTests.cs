using System.Text.RegularExpressions;

using Api.IntegrationTests.Infrastructure;

using Npgsql;

using Shouldly;

namespace Api.IntegrationTests;

/// <summary>
/// Сверка схемы, созданной EF Core-миграциями, с эталоном <c>db/schema.sql</c>.
/// </summary>
/// <remarks>
/// Тест помечен <c>Stage=final</c>: он станет зелёным к концу этапа 7, когда миграции
/// создадут все таблицы эталона. Обычный прогон идёт с <c>--filter "Stage!=final"</c>.
/// </remarks>
[Collection("postgres")]
public sealed partial class SchemaParityTests(PostgresFixture fixture)
{
    private const string ReferenceSchemaRelativePath = "db/schema.sql";

    [Fact]
    [Trait("Stage", "final")]
    public async Task All_tables_from_reference_schema_exist()
    {
        // xUnit v2 не передаёт CancellationToken в Fact; единая точка для таймаута/отмены.
        var cancellationToken = CancellationToken.None;

        var schemaPath = Path.Combine(FindRepositoryRoot(), ReferenceSchemaRelativePath);
        var schemaSql = await File.ReadAllTextAsync(schemaPath, cancellationToken);

        var expectedTables = CreateTableRegex()
            .Matches(schemaSql)
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        expectedTables.ShouldNotBeEmpty(
            $"Из '{schemaPath}' не извлечено ни одной таблицы: проверьте регулярное выражение и формат эталона.");

        var actualTables = await ReadPublicTablesAsync(cancellationToken);

        var missingTables = expectedTables
            .Except(actualTables, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();

        missingTables.ShouldBeEmpty(
            $"В БД нет таблиц из {ReferenceSchemaRelativePath}: {string.Join(", ", missingTables)}");
    }

    private async Task<HashSet<string>> ReadPublicTablesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            select table_name
            from information_schema.tables
            where table_schema = 'public'
              and table_type = 'BASE TABLE'
              -- Служебные таблицы, которых нет в эталоне: история миграций EF и PostGIS.
              and table_name not in ('__EFMigrationsHistory', 'spatial_ref_sys')
            """,
            connection);

        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    /// <summary>
    /// Ищет корень репозитория подъёмом вверх от каталога тестовой сборки:
    /// это каталог, в котором лежит <c>db/schema.sql</c>.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "db", "schema.sql")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Не найден {ReferenceSchemaRelativePath} ни в одном из родительских каталогов '{AppContext.BaseDirectory}'.");
    }

    [GeneratedRegex(
        """create\s+table\s+(?:if\s+not\s+exists\s+)?"?(?<name>[A-Za-z_][A-Za-z0-9_$]*)"?\s*\(""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateTableRegex();
}
