using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Api.IntegrationTests.Infrastructure;

using Microsoft.AspNetCore.Http;

using IdentityAccess.Contracts;

using ContractRoles = IdentityAccess.Contracts.Roles;

using Shouldly;

namespace Api.IntegrationTests.Compliance;

/// <summary>
/// Матрица прав «эндпоинт × роль». Ожидания генерируются из
/// <c>x-permission</c> контракта и <see cref="Roles.PermissionsFor"/>:
/// <list type="bullet">
/// <item>аноним без права получает 401, если метод защищён, и не получает 401/403, если он публичный;</item>
/// <item>роль без права получает 403;</item>
/// <item>роль с правом получает любой код, кроме 401/403 (200/204/404/409/422 и т. п.).</item>
/// </list>
/// Любой 500 — падение: тело запроса минимальное (пустой JSON-объект), поэтому сервер обязан
/// ответить 4xx, а не падать. Операции без права (<c>none</c>) проверяются только на отсутствие 500.
/// </summary>
[Collection("postgres")]
public sealed class PermissionMatrixTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string ApiPrefix = "/api/v1";

    private static readonly string[] RoleNames = ["user", "moderator", "admin"];

    private ComplianceApi _api = null!;
    private Dictionary<string, string?> _tokens = null!;

    public async Task InitializeAsync()
    {
        var ct = CancellationToken.None;

        _api = new ComplianceApi(fixture);
        await _api.InitializeAsync();

        _tokens = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["anonymous"] = null,
            ["user"] = await _api.UserTokenAsync(ct),
            ["moderator"] = await _api.ModeratorTokenAsync(ct),
            ["admin"] = await _api.AdminTokenAsync(ct),
        };
    }

    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    /// <summary>Все операции контракта: по одной теории на операцию, внутри — все четыре роли.</summary>
    public static TheoryData<string, string> Operations()
    {
        var data = new TheoryData<string, string>();

        foreach (var operation in ContractOperations.All)
        {
            data.Add(operation.Method, operation.Path);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Matrix_matches_contract_permissions_and_tz_3_3(
        string method,
        string pathTemplate)
    {
        var ct = CancellationToken.None;
        var operation = ContractOperations.All.Single(item =>
            item.Method == method && item.Path == pathTemplate);

        var failures = new List<string>();

        string[] allRoles = ["anonymous", .. RoleNames];

        foreach (var role in allRoles)
        {
            var status = await SendAsync(operation, _tokens[role], ct);

            if (status == StatusCodes.Status500InternalServerError)
            {
                failures.Add($"{operation.DisplayName} × {role}: 500");
                continue;
            }

            var expectation = Expect(operation, role);
            if (expectation is null)
            {
                // Операции с x-permission: none без bearer-схемы (например, refresh по cookie)
                // не проверяются на 401/403: bearer-токен к ним неприменим.
                continue;
            }

            if (!expectation(status))
            {
                failures.Add($"{operation.DisplayName} × {role}: {status}, ожидалось {Describe(operation, role)}");
            }
        }

        failures.ShouldBeEmpty(string.Join('\n', failures));
    }

    /// <summary>
    /// Ожидаемый класс ответа для роли или <c>null</c>, если контракт не позволяет его вывести
    /// (операции с <c>x-permission: none</c>, кроме публичных).
    /// </summary>
    private static Func<int, bool>? Expect(ContractOperation operation, string role)
    {
        var isKnownPermission = Permissions.All.Contains(operation.Permission, StringComparer.Ordinal);

        if (!isKnownPermission && operation.Permission is not ("auth" or "none"))
        {
            throw new InvalidOperationException(
                $"{operation.DisplayName}: неизвестное x-permission '{operation.Permission}'.");
        }

        if (operation.IsPublic)
        {
            // Публичный метод не должен требовать вход.
            return status => status is not (401 or 403);
        }

        if (isKnownPermission)
        {
            if (role == "anonymous")
            {
                return status => status == 401;
            }

            var allowed = ContractRoles.PermissionsFor(role).Contains(operation.Permission, StringComparer.Ordinal);

            return allowed
                ? status => status is not (401 or 403)
                : status => status == 403;
        }

        if (operation.Permission == "auth")
        {
            // /me и /auth/logout: любой вошедший пользователь, независимо от набора прав.
            return role == "anonymous" ? status => status == 401 : status => status is not (401 or 403);
        }

        return null;
    }

    private static string Describe(ContractOperation operation, string role)
    {
        if (operation.IsPublic)
        {
            return "не 401/403 (публичный)";
        }

        if (role == "anonymous")
        {
            return operation.Permission == "auth" ? "401 (нужен вход)" : "401";
        }

        if (operation.Permission == "auth")
        {
            return "не 401/403 (вошедший пользователь)";
        }

        var allowed = ContractRoles.PermissionsFor(role).Contains(operation.Permission, StringComparer.Ordinal);

        return allowed ? $"право {operation.Permission}: не 401/403" : $"нет права {operation.Permission}: 403";
    }

    private async Task<int> SendAsync(
        ContractOperation operation,
        string? accessToken,
        CancellationToken ct)
    {
        var url = ApiPrefix + ComplianceApi.ResolveWithPlaceholders(operation);

        if (operation.Path == "/map/features")
        {
            url += "?bbox=37.70,47.90,37.90,48.10";
        }

        using var request = new HttpRequestMessage(new HttpMethod(operation.Method), url);

        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (operation.Method is "POST" or "PUT" or "PATCH")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        using var response = await _api.Client.SendAsync(request, ct);

        return (int)response.StatusCode;
    }
}
