using System.Globalization;
using System.Text;
using System.Text.Json;

using BuildingBlocks.Security;

using Npgsql;

namespace Api.IntegrationTests.Ingestion;

/// <summary>Источник и привязанная фиксация, созданные тестом Worker-а напрямую в БД.</summary>
internal sealed record TestSource(Guid Id, Guid ObjectId, string Name, string ChatId, string ObjectName);

/// <summary>Строка <c>outbox_messages</c>, прочитанная для проверки payload.</summary>
internal sealed record OutboxRow(Guid Id, string Type, string Payload);

/// <summary>Состояние источника из <c>sources.state</c> и <c>state_detail</c>.</summary>
internal sealed record SourceHealthRow(string State, string? Detail);

/// <summary>
/// Прямой доступ к БД для тестов Worker-а: Worker не публикует HTTP-эндпоинтов, поэтому
/// настройки источников и проверки результата идут SQL-ом, как это делает сам Worker.
/// </summary>
internal static class WorkerDatabase
{
    private const string TestSourcePrefix = "worker-source-";
    private const string TestObjectPrefix = "worker-object-";

    /// <summary>
    /// Чистит данные предыдущих прогонов тестов Worker-а, чтобы фоновый watcher нового теста
    /// не запускал адаптеры для «осиротевших» источников. Коллекция тестов выполняется
    /// последовательно, поэтому удаление безопасно.
    /// </summary>
    public static async Task CleanupPreviousRunsAsync(
        string connectionString,
        CancellationToken ct = default)
    {
        await ExecuteAsync(
            connectionString,
            """
            delete from outbox_messages
            where destination = 'api'
              and type in ('ObservationProposed', 'ModerationCaseRequested')
              and payload ? 'sourceId';
            delete from sources where name like 'worker-source-%';
            delete from map_objects where name like 'worker-object-%';
            """,
            ct);
    }

    /// <summary>Создаёт источник с политикой, двумя статусами и одной привязанной фиксацией.</summary>
    public static async Task<TestSource> CreateSourceAsync(
        string connectionString,
        string sourceType = "telegram_chat",
        string? prompt = "Ищи сообщения о подаче воды",
        CancellationToken ct = default)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var id = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var name = $"{TestSourcePrefix}{suffix}";
        var objectName = $"{TestObjectPrefix}{suffix}";
        var chatId = $"@worker_{suffix}";

        await ExecuteAsync(
            connectionString,
            """
            insert into sources (id, name, source_type, settings, user_prompt, link_by_text, is_enabled)
            values (@id, @name, @source_type, jsonb_build_object('chatId', @chat_id), @prompt, true, true);

            insert into source_policies (source_id, timeout, confidence_threshold)
            values (@id, interval '1 hour', 0.5);

            insert into source_statuses (source_id, code, name, description, color, icon, sort_order)
            values
              (@id, 'ok', 'Вода есть', 'Подача воды идёт или возобновилась', 'ok', 'drop', 1),
              (@id, 'none', 'Воды нет', 'Подача воды прекращена', 'none', 'cross', 2);

            insert into map_objects (id, name, coverage_kind, geometry)
            values (@object_id, @object_name, 'polygon',
                    'POLYGON((37.78 48.00, 37.86 48.00, 37.86 48.05, 37.78 48.05, 37.78 48.00))'::geography);

            insert into map_object_sources (map_object_id, source_id) values (@object_id, @id);
            """,
            ct,
            ("id", id),
            ("name", name),
            ("source_type", sourceType),
            ("chat_id", chatId),
            ("prompt", prompt),
            ("object_id", objectId),
            ("object_name", objectName));

        return new TestSource(id, objectId, name, chatId, objectName);
    }

    /// <summary>Меняет промпт-фильтр источника.</summary>
    public static Task SetPromptAsync(
        string connectionString,
        Guid sourceId,
        string prompt,
        CancellationToken ct = default) =>
        ExecuteAsync(
            connectionString,
            "update sources set user_prompt = @prompt where id = @id",
            ct,
            ("prompt", prompt),
            ("id", sourceId));

    /// <summary>Меняет название и описание статуса источника.</summary>
    public static Task SetStatusAsync(
        string connectionString,
        Guid sourceId,
        string code,
        string name,
        string description,
        CancellationToken ct = default) =>
        ExecuteAsync(
            connectionString,
            """
            update source_statuses
            set name = @name, description = @description
            where source_id = @id and code = @code
            """,
            ct,
            ("name", name),
            ("description", description),
            ("id", sourceId),
            ("code", code));

    /// <summary>Выключает источник.</summary>
    public static Task DisableAsync(
        string connectionString,
        Guid sourceId,
        CancellationToken ct = default) =>
        ExecuteAsync(
            connectionString,
            "update sources set is_enabled = false where id = @id",
            ct,
            ("id", sourceId));

    /// <summary>Задаёт (заменяет) единственный секрет LLM с указанным ключом.</summary>
    public static async Task SetLlmSecretAsync(
        string connectionString,
        ISecretCipher cipher,
        string apiKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cipher);

        var payload = JsonSerializer.Serialize(
            new
            {
                apiKey,
                baseUrl = "https://llm.example/v1",
                model = "gpt-4o-mini",
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var ciphertext = cipher.Encrypt(
            Encoding.UTF8.GetBytes(payload),
            cipher.CurrentKeyVersion);

        await ExecuteAsync(
            connectionString,
            """
            delete from secrets where kind = 'llm';
            insert into secrets (id, kind, name, ciphertext, key_version)
            values (@id, 'llm', 'LLM', @ciphertext, @key_version);
            """,
            ct,
            ("id", Guid.NewGuid()),
            ("ciphertext", ciphertext),
            ("key_version", cipher.CurrentKeyVersion));
    }

    /// <summary>Читает состояние и деталь health источника.</summary>
    public static async Task<SourceHealthRow> ReadHealthAsync(
        string connectionString,
        Guid sourceId,
        CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            "select state, state_detail from sources where id = @id",
            connection);
        command.Parameters.AddWithValue("id", sourceId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new SourceHealthRow(string.Empty, null);
        }

        return new SourceHealthRow(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    /// <summary>Читает сообщения outbox, порождённые указанным источником.</summary>
    public static async Task<List<OutboxRow>> ReadOutboxAsync(
        string connectionString,
        Guid sourceId,
        CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select id, type, payload::text
            from outbox_messages
            where destination = 'api'
              and (payload->>'sourceId')::uuid = @source_id
            order by created_at, id
            """,
            connection);
        command.Parameters.AddWithValue("source_id", sourceId);

        var rows = new List<OutboxRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new OutboxRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    /// <summary>Идентификатор сохранённого <c>raw_messages</c> по внешнему идентификатору.</summary>
    public static async Task<Guid?> ReadRawMessageIdAsync(
        string connectionString,
        Guid sourceId,
        string externalId,
        CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            "select id from raw_messages where source_id = @source_id and external_id = @external_id",
            connection);
        command.Parameters.AddWithValue("source_id", sourceId);
        command.Parameters.AddWithValue("external_id", externalId);

        var result = await command.ExecuteScalarAsync(ct);

        return result is Guid id ? id : null;
    }

    /// <summary>Считает строки запросом с параметрами.</summary>
    public static async Task<long> CountAsync(
        string connectionString,
        string sql,
        CancellationToken ct = default,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var result = await command.ExecuteScalarAsync(ct);

        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        string connectionString,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(ct);
    }
}
