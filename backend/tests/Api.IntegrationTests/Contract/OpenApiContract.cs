using YamlDotNet.RepresentationModel;

namespace Api.IntegrationTests.Contract;

/// <summary>
/// Минимальная модель <c>api/openapi.yaml</c> для тестов соответствия контракту:
/// операции (метод + путь с префиксом <c>servers</c>), их <c>x-permission</c>, признак
/// публичности (<c>security: []</c>) и объявленные коды ответов.
/// </summary>
internal sealed class OpenApiContract
{
    private OpenApiContract(
        string apiPrefix,
        IReadOnlyList<OpenApiOperation> operations)
    {
        ApiPrefix = apiPrefix;
        Operations = operations;
        OperationsByRoute = operations.ToDictionary(
            operation => (operation.Method, operation.Path));
    }

    /// <summary>Базовый путь из относительного элемента <c>servers</c> (например, <c>/api/v1</c>).</summary>
    public string ApiPrefix { get; }

    /// <summary>Все операции контракта; <see cref="OpenApiOperation.Path"/> уже содержит префикс.</summary>
    public IReadOnlyList<OpenApiOperation> Operations { get; }

    /// <summary>Операции по паре метод+путь (метод — в верхнем регистре, путь нормализован).</summary>
    public IReadOnlyDictionary<(string Method, string Path), OpenApiOperation> OperationsByRoute { get; }

    /// <summary>
    /// Находит операцию для конкретного запроса: сначала точное совпадение, затем совпадение
    /// шаблона с параметрами (<c>/map/objects/{id}</c> ← <c>/map/objects/&lt;guid&gt;</c>).
    /// </summary>
    public OpenApiOperation? FindOperation(string method, string path)
    {
        var normalizedMethod = method.ToUpperInvariant();
        var normalizedPath = NormalizePath(path);

        if (OperationsByRoute.TryGetValue((normalizedMethod, normalizedPath), out var exact))
        {
            return exact;
        }

        var segments = SplitSegments(normalizedPath);

        return Operations.FirstOrDefault(operation =>
            string.Equals(operation.Method, normalizedMethod, StringComparison.Ordinal)
            && MatchesTemplate(operation.Path, segments));
    }

    private static bool MatchesTemplate(string templatePath, string[] pathSegments)
    {
        var templateSegments = SplitSegments(templatePath);

        if (templateSegments.Length != pathSegments.Length)
        {
            return false;
        }

        for (var index = 0; index < templateSegments.Length; index++)
        {
            var template = templateSegments[index];
            var actual = pathSegments[index];

            if (template.StartsWith('{') && template.EndsWith('}'))
            {
                if (actual.Length == 0)
                {
                    return false;
                }

                continue;
            }

            if (!string.Equals(template, actual, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string[] SplitSegments(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    public static OpenApiContract Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var stream = new YamlStream();
        using (var reader = File.OpenText(path))
        {
            stream.Load(reader);
        }

        if (stream.Documents.Count != 1)
        {
            throw new InvalidDataException(
                $"'{path}' должен содержать ровно один YAML-документ, найдено {stream.Documents.Count}.");
        }

        var root = stream.Documents[0].RootNode as YamlMappingNode
            ?? throw new InvalidDataException($"Корень '{path}' — не YAML-объект.");

        var apiPrefix = ReadApiPrefix(root);
        var globalSecurityIsPublic = IsPublicSecurity(GetChild(root, "security"));

        var paths = GetChild(root, "paths") as YamlMappingNode
            ?? throw new InvalidDataException($"В '{path}' нет секции paths.");

        var operations = new List<OpenApiOperation>();

        foreach (var (pathNode, pathValue) in paths.Children)
        {
            if (pathNode is not YamlScalarNode { Value: { } relativePath }
                || pathValue is not YamlMappingNode operationsNode)
            {
                continue;
            }

            foreach (var (methodNode, operationValue) in operationsNode.Children)
            {
                if (methodNode is not YamlScalarNode { Value: { } method }
                    || !IsHttpMethod(method)
                    || operationValue is not YamlMappingNode operationNode)
                {
                    continue;
                }

                var permission = GetScalar(operationNode, "x-permission")
                    ?? throw new InvalidDataException(
                        $"У операции {method.ToUpperInvariant()} {relativePath} нет x-permission.");

                // Отсутствие security у операции означает наследование глобальной security.
                var securityNode = GetChild(operationNode, "security");
                var isPublic = securityNode is null
                    ? globalSecurityIsPublic
                    : IsPublicSecurity(securityNode);

                operations.Add(new OpenApiOperation(
                    method.ToUpperInvariant(),
                    NormalizePath(apiPrefix + relativePath),
                    permission,
                    isPublic,
                    ReadStatusCodes(operationNode)));
            }
        }

        return new OpenApiContract(apiPrefix, operations);
    }

    /// <summary>
    /// Приводит шаблон маршрута к виду контракта: убирает ограничения параметров
    /// (<c>{id:guid}</c> → <c>{id}</c>), двойные и хвостовые слеши.
    /// </summary>
    public static string NormalizePath(string path)
    {
        var result = path.Trim();

        var brace = result.IndexOf('{', StringComparison.Ordinal);
        while (brace >= 0)
        {
            var end = result.IndexOf('}', brace);
            if (end < 0)
            {
                break;
            }

            var parameter = result[brace..(end + 1)];
            var colon = parameter.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0)
            {
                parameter = parameter[..colon] + "}";
                result = result[..brace] + parameter + result[(end + 1)..];
                end = brace + parameter.Length - 1;
            }

            brace = result.IndexOf('{', end + 1);
        }

        if (!result.StartsWith("/", StringComparison.Ordinal))
        {
            result = "/" + result;
        }

        return result.Length > 1 ? result.TrimEnd('/') : result;
    }

    private static bool IsHttpMethod(string value) =>
        value is "get" or "post" or "put" or "patch" or "delete" or "head" or "options" or "trace";

    private static string ReadApiPrefix(YamlMappingNode root)
    {
        if (GetChild(root, "servers") is not YamlSequenceNode servers)
        {
            return string.Empty;
        }

        foreach (var server in servers.Children)
        {
            if (server is YamlMappingNode serverNode
                && GetChild(serverNode, "url") is YamlScalarNode { Value: { } url }
                && url.StartsWith("/", StringComparison.Ordinal))
            {
                return NormalizePath(url) is "/" ? string.Empty : NormalizePath(url);
            }
        }

        return string.Empty;
    }

    private static bool IsPublicSecurity(YamlNode? securityNode) =>
        securityNode is YamlSequenceNode { Children.Count: 0 };

    private static IReadOnlyList<int> ReadStatusCodes(YamlMappingNode operationNode)
    {
        if (GetChild(operationNode, "responses") is not YamlMappingNode responses)
        {
            return [];
        }

        var statusCodes = new List<int>();

        foreach (var (statusNode, _) in responses.Children)
        {
            if (statusNode is not YamlScalarNode { Value: { } status })
            {
                continue;
            }

            if (int.TryParse(status, System.Globalization.CultureInfo.InvariantCulture, out var exact))
            {
                statusCodes.Add(exact);
            }
            else if (status.Length == 3
                && char.IsAsciiDigit(status[0])
                && status[1] is 'X' or 'x'
                && status[2] is 'X' or 'x')
            {
                // Диапазон вида 2XX: любой код этой сотни объявлен.
                for (var code = (status[0] - '0') * 100; code < (status[0] - '0') * 100 + 100; code++)
                {
                    statusCodes.Add(code);
                }
            }
        }

        return statusCodes;
    }

    private static YamlNode? GetChild(YamlMappingNode mapping, string key)
    {
        foreach (var (node, value) in mapping.Children)
        {
            if (node is YamlScalarNode { Value: { } scalar }
                && string.Equals(scalar, key, StringComparison.Ordinal))
            {
                return value;
            }
        }

        return null;
    }

    private static string? GetScalar(YamlMappingNode mapping, string key) =>
        GetChild(mapping, key) is YamlScalarNode { Value: { } value } ? value : null;
}

/// <summary>Операция контракта: метод, путь (с префиксом), право и объявленные ответы.</summary>
internal sealed record OpenApiOperation(
    string Method,
    string Path,
    string Permission,
    bool IsPublic,
    IReadOnlyList<int> StatusCodes);
