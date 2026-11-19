using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

using Api.IntegrationTests.Catalog;
using Api.IntegrationTests.Identity;
using Api.IntegrationTests.Infrastructure;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Shouldly;

namespace Api.IntegrationTests.Compliance;

/// <summary>
/// Операция контракта <c>api/openapi.yaml</c>: метод, путь, требуемое право и признак
/// публичности (<c>security: []</c>). Читается тестами из файла контракта, чтобы матрица
/// прав и обход эндпоинтов не дублировали его вручную.
/// </summary>
/// <param name="Method">HTTP-метод в верхнем регистре.</param>
/// <param name="Path">Шаблон пути без префикса <c>/api/v1</c>.</param>
/// <param name="Permission">Значение <c>x-permission</c>: код права, <c>auth</c> или <c>none</c>.</param>
/// <param name="IsPublic"><c>true</c>, если у операции <c>security: []</c>.</param>
internal sealed record ContractOperation(string Method, string Path, string Permission, bool IsPublic)
{
    /// <summary>Человекочитаемое имя операции для сообщений об ошибках.</summary>
    public string DisplayName => $"{Method} {Path}";
}

/// <summary>
/// Разбор контракта <c>api/openapi.yaml</c>. Используется простой построчный
/// разбор блока <c>paths</c>: методы в тестах нужны только метод, путь, <c>x-permission</c>
/// и <c>security</c>, поэтому полноценная YAML-библиотека не добавляется.
/// </summary>
internal static class ContractOperations
{
    private static readonly Regex PathRegex = new(@"^  (/\S+):\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex MethodRegex = new(
        @"^    (get|post|put|delete|patch):\s*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex PermissionRegex = new(
        @"^      x-permission:\s*(\S+)\s*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex SecurityRegex = new(
        @"^      security:\s*(.*)$",
        RegexOptions.CultureInvariant);

    /// <summary>Все операции контракта в порядке файла.</summary>
    public static IReadOnlyList<ContractOperation> All { get; } = Load();

    /// <summary>Только GET-операции: их ответы проверяются на и утечки секретов.</summary>
    public static IReadOnlyList<ContractOperation> Gets { get; } =
        [.. All.Where(operation => operation.Method == "GET")];

    /// <summary>Абсолютный путь к контракту: подъём от каталога тестовой сборки до корня репозитория.</summary>
    /// <exception cref="FileNotFoundException">Контракт не найден.</exception>
    public static string FindOpenApiPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "api", "openapi.yaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Не найден api/openapi.yaml ни в одном из родительских каталогов '{AppContext.BaseDirectory}'.");
    }

    private static List<ContractOperation> Load()
    {
        var lines = File.ReadAllLines(FindOpenApiPath());
        var operations = new List<ContractOperation>();
        string? currentPath = null;

        for (var index = 0; index < lines.Length; index++)
        {
            var pathMatch = PathRegex.Match(lines[index]);
            if (pathMatch.Success)
            {
                currentPath = pathMatch.Groups[1].Value;
                continue;
            }

            var methodMatch = MethodRegex.Match(lines[index]);
            if (!methodMatch.Success || currentPath is null)
            {
                continue;
            }

            string? permission = null;
            var isPublic = false;

            for (var inner = index + 1; inner < lines.Length; inner++)
            {
                var line = lines[inner];

                // Свойства операции имеют отступ 6 пробелов; всё, что меньше, — уже другой блок.
                if (!line.StartsWith("      ", StringComparison.Ordinal))
                {
                    break;
                }

                var permissionMatch = PermissionRegex.Match(line);
                if (permissionMatch.Success)
                {
                    permission = permissionMatch.Groups[1].Value;
                }

                var securityMatch = SecurityRegex.Match(line);
                if (securityMatch.Success)
                {
                    isPublic = string.Equals(
                        securityMatch.Groups[1].Value.Trim(),
                        "[]",
                        StringComparison.Ordinal);
                }
            }

            if (permission is null)
            {
                throw new InvalidOperationException(
                    $"У операции {methodMatch.Groups[1].Value.ToUpperInvariant()} {currentPath} "
                    + "нет x-permission.");
            }

            operations.Add(new ContractOperation(
                methodMatch.Groups[1].Value.ToUpperInvariant(),
                currentPath,
                permission,
                isPublic));
        }

        operations.ShouldNotBeEmpty(
            $"Из '{FindOpenApiPath()}' не разобрано ни одной операции контракта.");

        return operations;
    }
}

/// <summary>Засеянные данные, на которые тесты подставляют параметры путей контракта.</summary>
/// <param name="CategoryId">Категория (dev-seed «Вода»).</param>
/// <param name="ObjectTypeId">Тип объекта (dev-seed «Район»).</param>
/// <param name="GroupId">Созданная группа фиксаций.</param>
/// <param name="SourceId">Созданный источник.</param>
/// <param name="StatusId">Статус источника.</param>
/// <param name="ObjectId">Созданная фиксация.</param>
/// <param name="ObservationId">Наблюдение по фиксации.</param>
/// <param name="CaseId">Кейс модерации из жалобы.</param>
internal sealed record ComplianceData(
    Guid CategoryId,
    Guid ObjectTypeId,
    Guid GroupId,
    Guid SourceId,
    Guid StatusId,
    Guid ObjectId,
    Guid ObservationId,
    Guid CaseId);

/// <summary>
/// Хост Api на общей БД коллекции плюс данные для сквозных проверок: категория,
/// группа, источник со статусом, фиксация с адресом, наблюдение с комментарием и жалоба
/// (кейс модерации). Каждый тестовый класс создаёт свои данные с уникальным суффиксом,
/// поэтому другие тесты коллекции не влияют на проверки.
/// </summary>
internal sealed class ComplianceApi : IAsyncDisposable
{
    /// <summary>Dev-администратор (a5): все права, включая секреты и аудит.</summary>
    public static readonly Guid AdminId = Guid.Parse("00000000-0000-0000-0000-0000000000a5");

    /// <summary>Dev-пользователь (a1): карта и отметки.</summary>
    public static readonly Guid UserId = CatalogApi.UserId;

    /// <summary>Dev-модератор (a3): всё, кроме админских прав.</summary>
    public static readonly Guid ModeratorId = CatalogApi.ModeratorId;

    private readonly CatalogApi _api;

    public ComplianceApi(
        PostgresFixture fixture,
        Action<IServiceCollection>? configureServices = null,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        _api = new CatalogApi(fixture, configureServices, settings);
    }

    public TestApiFactory Factory => _api.Factory;

    public HttpClient Client => _api.Client;

    public Task InitializeAsync() => _api.InitializeAsync();

    public Task<string> UserTokenAsync(CancellationToken ct = default) => _api.UserTokenAsync(ct);

    public Task<string> ModeratorTokenAsync(CancellationToken ct = default) =>
        _api.ModeratorTokenAsync(ct);

    public Task<string> AdminTokenAsync(CancellationToken ct = default) =>
        TestAuth.CreateAccessTokenAsync(Factory.Services, AdminId, ct);

    /// <summary>Токен по имени роли из матрицы (<c>user</c>, <c>moderator</c>, <c>admin</c>).</summary>
    public Task<string> TokenAsync(string role, CancellationToken ct = default) => role switch
    {
        "user" => UserTokenAsync(ct),
        "moderator" => ModeratorTokenAsync(ct),
        "admin" => AdminTokenAsync(ct),
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Неизвестная роль."),
    };

    public Task<HttpResponseMessage> GetAsync(
        string path,
        string? accessToken = null,
        CancellationToken ct = default) =>
        _api.GetAsync(path, accessToken, ct);

    public Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? accessToken,
        string? json,
        CancellationToken ct = default) =>
        _api.SendAsync(method, path, accessToken, json, ct);

    public Task ExecuteAsync(string sql, CancellationToken ct = default) => _api.ExecuteAsync(sql, ct);

    public Task<long> CountAsync(string sql, CancellationToken ct = default) => _api.CountAsync(sql, ct);

    /// <summary>
    /// Создаёт данные во всех публичных областях: каталог (группа), источник со статусом,
    /// фиксацию с адресом, наблюдение с комментарием и жалобу (кейс модерации).
    /// </summary>
    public async Task<ComplianceData> SeedAsync(CancellationToken ct)
    {
        var moderator = await ModeratorTokenAsync(ct);
        var user = await UserTokenAsync(ct);
        var suffix = CatalogApi.NewSuffix();

        var groupId = await CreateAsync(
            "/api/v1/catalog/groups",
            moderator,
            $$"""{"name":"Группа {{suffix}}","color":"blue"}""",
            ct);

        var sourceId = await CreateAsync(
            "/api/v1/sources",
            moderator,
            $$"""
              {"name":"Источник {{suffix}}","sourceType":"telegram_chat",
               "chatId":"@compliance_{{suffix}}","timeoutSeconds":3600,
               "userPrompt":"Ищи упоминания подачи воды"}
              """,
            ct);

        var statusId = await CreateAsync(
            $"/api/v1/sources/{sourceId}/statuses",
            moderator,
            $$"""
              {"code":"water_ok","name":"Вода есть","description":"Подача воды идёт",
               "color":"ok","icon":"drop"}
              """,
            ct);

        var objectId = await CreateAsync(
            "/api/v1/catalog/objects",
            moderator,
            $$"""
              {"name":"Фиксация {{suffix}}","groupId":"{{groupId}}","objectTypeId":"00000000-0000-0000-0000-00000000aa01","coverageKind":"point",
               "address":"ул. Центральная, 1",
               "geometry":{"type":"Point","coordinates":[37.80,48.01]},
               "sourceIds":["{{sourceId}}"]}
              """,
            ct);

        Guid observationId;
        using (var response = await SendAsync(
            HttpMethod.Post,
            "/api/v1/observations",
            user,
            $$"""
              {"mapObjectId":"{{objectId}}","sourceId":"{{sourceId}}",
               "sourceStatusId":"{{statusId}}","comment":"Подача воды идёт"}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);

            observationId = (await CatalogApi.ReadJsonAsync(response, ct))
                .GetProperty("observation").GetProperty("id").GetGuid();
        }

        Guid caseId;
        using (var response = await SendAsync(
            HttpMethod.Post,
            $"/api/v1/objects/{objectId}/complaints",
            user,
            """{"text":"Статус на карте не соответствует действительности"}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);

            caseId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("caseId").GetGuid();
        }

        return new ComplianceData(
            Guid.Parse("00000000-0000-0000-0000-0000000000c1"),
            Guid.Parse("00000000-0000-0000-0000-0000000000b1"),
            groupId,
            sourceId,
            statusId,
            objectId,
            observationId,
            caseId);
    }

    /// <summary>
    /// Подставляет в шаблон пути идентификаторы засеянных данных: кейс — для
    /// <c>/moderation/cases/{id}</c>, источник — для <c>/sources/{id}</c> и
    /// <c>/admin/sources/{id}</c>, фиксация — для остальных <c>{id}</c>.
    /// </summary>
    public static string ResolvePath(ContractOperation operation, ComplianceData data)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(data);

        var path = operation.Path;

        if (path.StartsWith("/moderation/cases/", StringComparison.Ordinal))
        {
            path = path.Replace("{id}", data.CaseId.ToString(), StringComparison.Ordinal);
        }
        else if (path.StartsWith("/sources/", StringComparison.Ordinal)
            || path.StartsWith("/admin/sources/", StringComparison.Ordinal))
        {
            path = path.Replace("{id}", data.SourceId.ToString(), StringComparison.Ordinal);
        }
        else
        {
            path = path.Replace("{id}", data.ObjectId.ToString(), StringComparison.Ordinal);
        }

        path = path
            .Replace("{sourceId}", data.SourceId.ToString(), StringComparison.Ordinal)
            .Replace("{statusId}", data.StatusId.ToString(), StringComparison.Ordinal);

        // bbox обязателен для карты; область покрывает засеянную точку (37.80, 48.01).
        return path == "/map/features" ? $"{path}?bbox=37.70,47.90,37.90,48.10" : path;
    }

    /// <summary>
    /// Подставляет в шаблон пути заведомо несуществующие идентификаторы — для матрицы прав,
    /// где важен только класс ответа (401/403 против «доступ разрешён»).
    /// </summary>
    public static string ResolveWithPlaceholders(ContractOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return operation.Path
            .Replace("{id}", Guid.NewGuid().ToString(), StringComparison.Ordinal)
            .Replace("{sourceId}", Guid.NewGuid().ToString(), StringComparison.Ordinal)
            .Replace("{statusId}", Guid.NewGuid().ToString(), StringComparison.Ordinal);
    }

    private async Task<Guid> CreateAsync(
        string path,
        string accessToken,
        string json,
        CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, path, accessToken, json, ct);

        response.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            $"{path} должен создавать запись, ответ: {await response.Content.ReadAsStringAsync(ct)}");

        return (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();
}

/// <summary>
/// Рекурсивный обход JSON: все строковые значения и имена свойств (ключи). Используется
/// проверками и утечек секретов: ответ API не должен содержать запрещённый
/// текст ни в значении, ни в ключе.
/// </summary>
internal static class JsonTextWalker
{
    /// <summary>Все тексты JSON с путём до них; ключи помечены суффиксом <c>(ключ)</c>.</summary>
    public static IEnumerable<(string Path, string Text)> Enumerate(JsonElement element)
    {
        foreach (var item in Walk(element, "$"))
        {
            yield return item;
        }
    }

    private static IEnumerable<(string Path, string Text)> Walk(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return ($"{path}.{property.Name} (ключ)", property.Name);

                    foreach (var item in Walk(property.Value, $"{path}.{property.Name}"))
                    {
                        yield return item;
                    }
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var text in Walk(item, $"{path}[{index}]"))
                    {
                        yield return text;
                    }

                    index++;
                }

                break;

            case JsonValueKind.String:
                yield return (path, element.GetString() ?? string.Empty);
                break;
        }
    }
}

/// <summary>Проверки Problem Details (RFC 7807) для ответов об ошибках.</summary>
internal static class ProblemAssertions
{
    /// <summary>Тип содержимого всех ошибок по контракту.</summary>
    public const string ProblemJson = "application/problem+json";

    /// <summary>
    /// Признаки внутренних деталей, которых не должно быть в теле ошибки: стек, имена
    /// исключений, строка подключения, SQL. Совпадение — дефект раскрытия реализации.
    /// </summary>
    private static readonly string[] ForbiddenFragments =
    [
        "   at ",
        "System.",
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "DbUpdateException",
        "StackTrace",
        "Server=",
        "Host=",
        "Username=",
        "Password=",
        "select ",
        "SELECT ",
        "insert into",
        "INSERT INTO",
    ];

    /// <summary>
    /// Проверяет, что ответ — <c>application/problem+json</c> с <c>title</c> и <c>status</c>,
    /// не содержит стека, SQL и строки подключения; возвращает разобранное тело.
    /// </summary>
    public static async Task<JsonElement> AssertProblemAsync(
        HttpResponseMessage response,
        int expectedStatus,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        ((int)response.StatusCode).ShouldBe(
            expectedStatus,
            $"Ожидался {expectedStatus}, получен {(int)response.StatusCode}: "
            + await response.Content.ReadAsStringAsync(ct));

        response.Content.Headers.ContentType?.MediaType.ShouldBe(ProblemJson);

        var text = await response.Content.ReadAsStringAsync(ct);

        foreach (var fragment in ForbiddenFragments)
        {
            text.ShouldNotContain(fragment, Case.Sensitive);
        }

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement.Clone();

        root.GetProperty("title").GetString().ShouldNotBeNullOrWhiteSpace();
        root.GetProperty("status").GetInt32().ShouldBe(expectedStatus);

        return root;
    }

    /// <summary>Проверяет 422 по схеме <c>ValidationProblem</c> и наличие ошибки по полю.</summary>
    public static async Task<JsonElement> AssertValidationAsync(
        HttpResponseMessage response,
        string expectedField,
        CancellationToken ct = default)
    {
        var root = await AssertProblemAsync(response, StatusCodes.Status422UnprocessableEntity, ct);

        var errors = root.GetProperty("errors");
        errors.ValueKind.ShouldBe(JsonValueKind.Array);
        errors.GetArrayLength().ShouldBeGreaterThan(0);

        errors.EnumerateArray().ShouldContain(error =>
            error.GetProperty("field").GetString() == expectedField);

        foreach (var error in errors.EnumerateArray())
        {
            error.GetProperty("field").GetString().ShouldNotBeNullOrWhiteSpace();
            error.GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace();
        }

        return root;
    }

    /// <summary>422 для поля, имя которого начинается с ожидаемого (вложенные ошибки).</summary>
    public static async Task AssertValidationPrefixAsync(
        HttpResponseMessage response,
        string expectedPrefix,
        CancellationToken ct = default)
    {
        var root = await AssertProblemAsync(response, StatusCodes.Status422UnprocessableEntity, ct);

        root.GetProperty("errors").EnumerateArray().ShouldContain(error =>
            (error.GetProperty("field").GetString() ?? string.Empty)
                .StartsWith(expectedPrefix, StringComparison.Ordinal));
    }
}

/// <summary>
/// Перехват журнала хоста: копит отформатированные записи всех категорий, чтобы тест мог
/// убедиться, что значения секретов не попали в логи ни при одном запросе.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyCollection<string> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            var message = formatter(state, exception);

            entries.Enqueue($"{logLevel} {category}: {message}");

            if (exception is not null)
            {
                entries.Enqueue($"{logLevel} {category}: {exception}");
            }
        }
    }
}
