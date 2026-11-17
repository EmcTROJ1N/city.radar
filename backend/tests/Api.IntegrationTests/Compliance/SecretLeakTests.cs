using System.Net;
using System.Text;
using System.Text.Json;

using Api.IntegrationTests.Catalog;
using Api.IntegrationTests.Infrastructure;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using Shouldly;

namespace Api.IntegrationTests.Compliance;

/// <summary>
/// Секреты не покидают сервер. Тест записывает известные
/// значения (сессия Telegram, пароль прокси, ключ LLM) через write-only админские методы, затем
/// обходит все GET-методы контракта под администратором и проверяет, что ни в одном ответе,
/// журнале хоста или записи аудита нет ни значения, ни его base64/hex-представления; в БД
/// лежит шифртекст, не равный значению. <c>GET /admin/secrets</c> отдаёт только метаданные.
/// </summary>
[Collection("postgres")]
public sealed class SecretLeakTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly CapturingLoggerProvider _logs = new();

    private ComplianceApi _api = null!;
    private ComplianceData _data = null!;
    private string _adminToken = null!;

    public async Task InitializeAsync()
    {
        var ct = CancellationToken.None;

        _api = new ComplianceApi(
            fixture,
            services => services.AddSingleton<ILoggerProvider>(_logs));
        await _api.InitializeAsync();

        _data = await _api.SeedAsync(ct);
        _adminToken = await _api.AdminTokenAsync(ct);
    }

    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task Secret_values_are_not_leaked_to_responses_logs_or_audit()
    {
        var ct = CancellationToken.None;
        var secrets = await PutSecretsAsync(ct);
        var texts = new List<(string Context, string Body)>(secrets.PutBodies);

        // 1. Все GET-методы контракта под администратором: значения не должны появляться нигде.
        foreach (var operation in ContractOperations.Gets)
        {
            var path = "/api/v1" + ComplianceApi.ResolvePath(operation, _data);

            using var response = await _api.GetAsync(path, _adminToken, ct);

            response.StatusCode.ShouldBe(
                HttpStatusCode.OK,
                $"{operation.DisplayName}: ожидался 200, получен {(int)response.StatusCode}");

            texts.Add((operation.DisplayName, await response.Content.ReadAsStringAsync(ct)));
        }

        var leaks = new List<string>();

        foreach (var value in secrets.Values)
        {
            foreach (var (label, representation) in Representations(value.Value))
            {
                foreach (var (context, body) in texts)
                {
                    if (body.Contains(representation, StringComparison.Ordinal))
                    {
                        leaks.Add($"{context}: найдено представление «{label}» секрета {value.Kind}");
                    }
                }
            }
        }

        leaks.ShouldBeEmpty("Значения секретов попали в ответы API:\n" + string.Join('\n', leaks));

        // 2. В БД — только шифртекст, не равный значению.
        foreach (var secret in secrets.Stored)
        {
            var ciphertext = await ReadCiphertextAsync(secret.Id, ct);

            ciphertext.ShouldNotBeEmpty($"Секрет {secret.Kind} не сохранён.");
            ciphertext.ShouldNotBe(
                Encoding.UTF8.GetBytes(secret.Value),
                $"Секрет {secret.Kind} лежит в БД открытым текстом.");

            Encoding.UTF8.GetString(ciphertext).ShouldNotContain(
                secret.Value,
                Case.Sensitive,
                $"Шифртекст секрета {secret.Kind} содержит значение.");
        }

        // 3. Аудит (before/after) не содержит значений секретов.
        foreach (var value in secrets.Values)
        {
            foreach (var (label, representation) in Representations(value.Value))
            {
                var matches = await CountAsync(
                    """
                    select count(*) from audit_entries
                    where coalesce(before::text, '') || ' ' || coalesce(after::text, '')
                          like '%' || @value || '%'
                    """,
                    ct,
                    ("value", representation));

                matches.ShouldBe(
                    0L,
                    $"Аудит содержит представление «{label}» секрета {value.Kind}.");
            }
        }

        // 4. Журнал хоста за весь прогон (включая старт, миграции и все запросы).
        var logEntries = _logs.Entries;
        logEntries.ShouldNotBeEmpty("Перехват журнала не получил ни одной записи.");

        var logLeaks = new List<string>();

        foreach (var value in secrets.Values)
        {
            foreach (var (label, representation) in Representations(value.Value))
            {
                foreach (var entry in logEntries)
                {
                    if (entry.Contains(representation, StringComparison.Ordinal))
                    {
                        logLeaks.Add($"журнал: представление «{label}» секрета {value.Kind}: {entry}");
                    }
                }
            }
        }

        logLeaks.ShouldBeEmpty("Значения секретов попали в журнал хоста:\n" + string.Join('\n', logLeaks));
    }

    [Fact]
    public async Task Secrets_list_returns_only_metadata()
    {
        var ct = CancellationToken.None;
        var secrets = await PutSecretsAsync(ct);
        var allowedFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "kind", "name", "hasValue", "updatedAt", "proxyId", "mode",
        };

        using var response = await _api.GetAsync("/api/v1/admin/secrets", _adminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var items = document.RootElement;

        items.ValueKind.ShouldBe(JsonValueKind.Array);
        items.GetArrayLength().ShouldBeGreaterThanOrEqualTo(3);

        var ids = secrets.Stored.Select(secret => secret.Id).ToHashSet();

        foreach (var item in items.EnumerateArray())
        {
            foreach (var property in item.EnumerateObject())
            {
                allowedFields.ShouldContain(
                    property.Name,
                    $"GET /admin/secrets вернул лишнее поле «{property.Name}»");
            }

            if (!ids.Contains(item.GetProperty("id").GetGuid()))
            {
                continue;
            }

            item.TryGetProperty("hasValue", out var hasValue).ShouldBeTrue();
            hasValue.GetBoolean().ShouldBeTrue();
            item.TryGetProperty("ciphertext", out _).ShouldBeFalse();
            item.TryGetProperty("keyVersion", out _).ShouldBeFalse();
        }

        foreach (var secret in secrets.Values)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            foreach (var (label, representation) in Representations(secret.Value))
            {
                body.ShouldNotContain(
                    representation,
                    Case.Sensitive,
                    $"GET /admin/secrets вернул значение секрета ({label}).");
            }
        }
    }

    // ------------------------------------------------------------------
    // Данные и запросы
    // ------------------------------------------------------------------

    private async Task<SecretsSetup> PutSecretsAsync(CancellationToken ct)
    {
        var suffix = CatalogApi.NewSuffix();
        var sessionValue = $"SESSION-LEAK-{suffix}-ABCDEF";
        var proxyPassword = $"PROXY-LEAK-{suffix}";
        var llmKey = $"LLM-LEAK-{suffix}";
        var sessionId = Guid.NewGuid();
        var proxyId = Guid.NewGuid();
        var bodies = new List<(string Context, string Body)>();

        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"/api/v1/admin/secrets/telegram-sessions/{sessionId}",
            _adminToken,
            $$"""{"name":"Сессия {{suffix}}","session":"{{sessionValue}}"}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            bodies.Add(("PUT telegram-sessions", await response.Content.ReadAsStringAsync(ct)));
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            $"/api/v1/admin/secrets/proxies/{proxyId}",
            _adminToken,
            $$"""
              {"name":"Прокси {{suffix}}","scheme":"socks5","host":"proxy.example",
               "port":1080,"username":"mayak","password":"{{proxyPassword}}"}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            bodies.Add(("PUT proxies", await response.Content.ReadAsStringAsync(ct)));
        }

        using (var response = await _api.SendAsync(
            HttpMethod.Put,
            "/api/v1/admin/secrets/llm",
            _adminToken,
            $$"""
              {"apiKey":"{{llmKey}}","baseUrl":"https://llm.example/v1",
               "model":"mayak-test","proxyId":null}
              """,
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            bodies.Add(("PUT llm", await response.Content.ReadAsStringAsync(ct)));
        }

        var llmId = await ReadLlmSecretIdAsync(ct);

        var values = new List<SecretValue>
        {
            new("telegram_session", sessionValue),
            new("proxy", proxyPassword),
            new("llm", llmKey),
        };

        var stored = new List<StoredSecret>
        {
            new(sessionId, "telegram_session", sessionValue),
            new(proxyId, "proxy", proxyPassword),
            new(llmId, "llm", llmKey),
        };

        return new SecretsSetup(values, stored, bodies);
    }

    private async Task<Guid> ReadLlmSecretIdAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            "select id from secrets where kind = 'llm' limit 1",
            connection);

        var value = await command.ExecuteScalarAsync(ct);

        value.ShouldNotBeNull("Секрет LLM не сохранён.");

        return (Guid)value;
    }

    private async Task<byte[]> ReadCiphertextAsync(Guid id, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            "select ciphertext from secrets where id = @id",
            connection);
        command.Parameters.AddWithValue("id", id);

        var value = await command.ExecuteScalarAsync(ct);

        value.ShouldNotBeNull($"Секрет {id} не найден.");

        return (byte[])value;
    }

    private async Task<long> CountAsync(
        string sql,
        CancellationToken ct,
        params (string Name, string Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(ct),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Значение секрета и его типовые кодировки, которые тоже не должны утекать.</summary>
    private static IEnumerable<(string Label, string Representation)> Representations(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);

        yield return ("значение", value);
        yield return ("base64", Convert.ToBase64String(bytes));
        yield return ("hex", Convert.ToHexString(bytes));
        yield return ("hex-lower", Convert.ToHexString(bytes).ToLowerInvariant());
    }

    private sealed record SecretValue(string Kind, string Value);

    private sealed record StoredSecret(Guid Id, string Kind, string Value);

    private sealed record SecretsSetup(
        IReadOnlyList<SecretValue> Values,
        IReadOnlyList<StoredSecret> Stored,
        IReadOnlyList<(string Context, string Body)> PutBodies);
}
