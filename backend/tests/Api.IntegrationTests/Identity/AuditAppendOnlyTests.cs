using Api.IntegrationTests.Infrastructure;

using Npgsql;

using Shouldly;

namespace Api.IntegrationTests.Identity;

/// <summary>
/// Append-only журнал аудита: триггер <c>audit_entries_append_only</c> из миграции
/// <c>Identity</c> запрещает UPDATE и DELETE, а таблицы Identity &amp; Access созданы.
/// </summary>
[Collection("postgres")]
public sealed class AuditAppendOnlyTests(PostgresFixture fixture)
{
    private static readonly string[] ExpectedIdentityTables =
    [
        "users",
        "roles",
        "user_roles",
        "role_claims",
        "refresh_tokens",
        "audit_entries",
        "global_settings",
    ];

    [Fact]
    public async Task Identity_tables_exist()
    {
        // xUnit v2 не передаёт CancellationToken в Fact; единая точка для таймаута/отмены.
        var cancellationToken = CancellationToken.None;

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            select table_name
            from information_schema.tables
            where table_schema = 'public'
              and table_type = 'BASE TABLE'
            """,
            connection);

        var tables = new HashSet<string>(StringComparer.Ordinal);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        foreach (var table in ExpectedIdentityTables)
        {
            tables.ShouldContain(table, $"В схеме нет таблицы '{table}' из миграции Identity.");
        }
    }

    [Fact]
    public async Task Audit_entry_cannot_be_updated_or_deleted()
    {
        var cancellationToken = CancellationToken.None;

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var entryId = await InsertAuditEntryAsync(connection, cancellationToken);
        entryId.ShouldBeGreaterThan(0L);

        var updateException = await Should.ThrowAsync<PostgresException>(
            () => ExecuteAsync(
                connection,
                "update audit_entries set reason = 'changed' where id = @id",
                entryId,
                cancellationToken));

        updateException.SqlState.ShouldBe(PostgresErrorCodes.RaiseException);
        updateException.MessageText.ShouldContain("только для добавления записей");

        var deleteException = await Should.ThrowAsync<PostgresException>(
            () => ExecuteAsync(
                connection,
                "delete from audit_entries where id = @id",
                entryId,
                cancellationToken));

        deleteException.SqlState.ShouldBe(PostgresErrorCodes.RaiseException);
        deleteException.MessageText.ShouldContain("только для добавления записей");
    }

    private static async Task<long> InsertAuditEntryAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into audit_entries (action, reason)
            values ('identity.test', 'seed')
            returning id
            """,
            connection);

        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        long entryId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", entryId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
