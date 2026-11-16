using System.Reflection;

using NetArchTest.Rules;

using Shouldly;

using Xunit.Abstractions;

namespace Architecture.Tests;

/// <summary>
/// Границы модулей: чужой модуль виден только через <c>*.Contracts</c>,
/// домен ядра не зависит от EF Core и ASP.NET Core.
/// </summary>
/// <remarks>
/// Пока проекты пустые, NetArchTest получает пустой набор типов и правило считается выполненным.
/// Это ожидаемо: тесты становятся содержательными по мере появления типов в сборках.
/// </remarks>
public sealed class ModuleBoundaryTests(ITestOutputHelper output)
{
    private static readonly string[] Modules =
    [
        "IdentityAccess",
        "Catalog",
        "Observations",
        "Moderation",
        "Sources",
        "MapReadModel",
    ];

    /// <summary>Слои модуля, на которые нельзя ссылаться изнутри другого модуля.</summary>
    private static readonly string[] ForbiddenLayers = ["Core", "Application", "Infrastructure", "Domain"];

    private static readonly string[] ModuleAssemblies =
    [
        "IdentityAccess.Contracts",
        "IdentityAccess.Core",
        "Catalog.Contracts",
        "Catalog.Core",
        "Observations.Contracts",
        "Observations.Domain",
        "Observations.Application",
        "Observations.Infrastructure",
        "Moderation.Contracts",
        "Moderation.Core",
        "Sources.Contracts",
        "Sources.Core",
        "MapReadModel.Contracts",
        "MapReadModel.Core",
    ];

    private static readonly string[] IngestionAssemblies =
    [
        "Ingestion.Abstractions",
        "Ingestion.Pipeline",
        "Ingestion.Adapters.TelegramBotApi",
        "Ingestion.Adapters.TelegramMtProto",
        "Ingestion.Llm.OpenAi",
    ];

    private static readonly string[] BuildingBlocksAssemblies =
    [
        "BuildingBlocks.Persistence",
        "BuildingBlocks.Messaging",
        "BuildingBlocks.Security",
        "BuildingBlocks.Web",
    ];

    private static readonly string[] AllSourceAssemblies =
        [.. ModuleAssemblies, .. IngestionAssemblies, .. BuildingBlocksAssemblies];

    public static TheoryData<string> SourceAssemblyNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in AllSourceAssemblies)
        {
            data.Add(name);
        }

        return data;
    }

    public static TheoryData<string, string[]> ModuleAssemblyRules()
    {
        var data = new TheoryData<string, string[]>();

        foreach (var module in Modules)
        {
            var forbidden = ForbiddenNamespacesFor(module);

            // У Observations нет Core: внутренние слои — Application и Infrastructure.
            if (module == "Observations")
            {
                data.Add("Observations.Application", forbidden);
                data.Add("Observations.Infrastructure", forbidden);
            }
            else
            {
                data.Add($"{module}.Core", forbidden);
            }
        }

        return data;
    }

    public static TheoryData<string> ContractsAssemblyNames()
    {
        var data = new TheoryData<string>();
        foreach (var module in Modules)
        {
            data.Add($"{module}.Contracts");
        }

        return data;
    }

    /// <summary>
    /// Список модулей и сборок корректен: каждая сборка загружается по имени без исключения.
    /// </summary>
    [Theory]
    [MemberData(nameof(SourceAssemblyNames))]
    public void Source_assembly_is_loadable(string assemblyName)
    {
        var assembly = Assembly.Load(new AssemblyName(assemblyName));

        assembly.GetName().Name.ShouldBe(assemblyName);
    }

    /// <summary>
    /// Страховка от опечаток в списке: у каждого модуля есть Contracts и хотя бы один слой реализации.
    /// </summary>
    [Fact]
    public void Module_list_contains_all_expected_modules()
    {
        Modules.ShouldBe(
            ["IdentityAccess", "Catalog", "Observations", "Moderation", "Sources", "MapReadModel"]);

        foreach (var module in Modules)
        {
            AllSourceAssemblies.ShouldContain($"{module}.Contracts");
        }

        AllSourceAssemblies.ShouldContain("Observations.Application");
        AllSourceAssemblies.ShouldContain("Observations.Infrastructure");
        AllSourceAssemblies.ShouldContain("Observations.Domain");
    }

    /// <summary>
    /// <c>X.Core</c> (а для ядра — Application/Infrastructure) не зависит от внутренностей другого модуля.
    /// </summary>
    [Theory]
    [MemberData(nameof(ModuleAssemblyRules))]
    public void Module_core_does_not_reference_other_module_core(string sourceAssembly, string[] forbiddenNamespaces)
    {
        var assembly = TryLoadAnalyzableAssembly(sourceAssembly);
        if (assembly is null)
        {
            return;
        }

        AssertNoDependencies(assembly, forbiddenNamespaces);
    }

    /// <summary>Публичный контракт модуля не тянет за собой реализацию ни одного модуля.</summary>
    [Theory]
    [MemberData(nameof(ContractsAssemblyNames))]
    public void Contracts_do_not_depend_on_Core(string contractsAssembly)
    {
        var assembly = TryLoadAnalyzableAssembly(contractsAssembly);
        if (assembly is null)
        {
            return;
        }

        AssertNoDependencies(assembly, ForbiddenNamespacesFor(null));
    }

    /// <summary>Домен ядра — чистый C#: без EF Core, ASP.NET Core и Npgsql.</summary>
    [Fact]
    public void Domain_does_not_depend_on_EfCore_or_AspNet()
    {
        var assembly = TryLoadAnalyzableAssembly("Observations.Domain");
        if (assembly is null)
        {
            return;
        }

        AssertNoDependencies(
            assembly,
            ["Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql"]);
    }

    /// <summary>
    /// Загружает сборку по имени. Пустая или незагружаемая сборка не валит тест границ —
    /// за загрузку отвечает <see cref="Source_assembly_is_loadable"/>.
    /// </summary>
    private Assembly? TryLoadAnalyzableAssembly(string assemblyName)
    {
        Assembly assembly;
        try
        {
            assembly = Assembly.Load(new AssemblyName(assemblyName));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            output.WriteLine($"Сборка '{assemblyName}' не загружена ({ex.GetType().Name}); проверка границ пропущена.");
            return null;
        }

        if (assembly.GetTypes().Length == 0)
        {
            output.WriteLine($"В сборке '{assemblyName}' пока нет типов; проверка границ пропущена.");
            return null;
        }

        return assembly;
    }

    private static void AssertNoDependencies(Assembly assembly, IReadOnlyCollection<string> forbiddenNamespaces)
    {
        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny([.. forbiddenNamespaces])
            .GetResult();

        var failingTypes = result.FailingTypeNames ?? [];

        result.IsSuccessful.ShouldBeTrue(
            $"Сборка '{assembly.GetName().Name}' зависит от запрещённых пространств имён. " +
            $"Нарушающие типы: {string.Join(", ", failingTypes)}");
    }

    /// <summary>
    /// Запрещённые пространства имён всех модулей, кроме <paramref name="exceptModule"/>
    /// (<c>null</c> — всех модулей).
    /// </summary>
    private static string[] ForbiddenNamespacesFor(string? exceptModule) =>
        Modules
            .Where(module => module != exceptModule)
            .SelectMany(module => ForbiddenLayers.Select(layer => $"{module}.{layer}"))
            .ToArray();
}
