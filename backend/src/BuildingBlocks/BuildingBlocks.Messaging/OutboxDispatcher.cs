using System.Data;
using System.Data.Common;
using System.Text.Json;

using BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Messaging;

/// <summary>
/// Фоновый диспетчер transactional outbox для одного destination (решение).
/// </summary>
/// <remarks>
/// <para>
/// Пачка необработанных сообщений выбирается сырым SQL под <c>FOR UPDATE SKIP LOCKED</c> в
/// транзакции: два диспетчера (в том числе в разных процессах) не обработают одно сообщение
/// дважды, а медленная обработка не блокирует остальные сообщения.
/// </para>
/// <para>
/// Порядок — <c>available_at, id</c>. Успех фиксируется в <c>processed_at</c>; ошибка обработчика
/// (или отсутствие обработчика для типа) увеличивает <c>attempts</c>, пишет <c>last_error</c> и
/// откладывает <c>available_at</c> на <c>min(2^attempts, MaxBackoff)</c>. После
/// <see cref="OutboxDispatcherOptions.MaxAttempts"/> сообщение остаётся необработанным с ошибкой
/// и исключается из выборки — «отравленное» сообщение не блокирует очередь.
/// </para>
/// <para>
/// Время берётся только из <see cref="TimeProvider"/> (не <c>DateTime.UtcNow</c>), поэтому
/// задержки повторов проверяемы тестами с управляемыми часами.
/// </para>
/// </remarks>
public sealed class OutboxDispatcher : BackgroundService
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly OutboxDispatcherOptions _options;
    private readonly ILogger<OutboxDispatcher> _logger;

    public OutboxDispatcher(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        OutboxDispatcherOptions options,
        ILogger<OutboxDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        options.Validate();

        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Обрабатывает одну пачку сообщений своего destination. Публичный, чтобы тесты и
    /// диагностика запускали диспетчер вручную, без ожидания <see cref="BackgroundService"/>.
    /// </summary>
    /// <returns>Количество успешно обработанных сообщений.</returns>
    public async Task<int> DispatchOnceAsync(CancellationToken cancellationToken)
    {
        // Scope на пачку: обработчики и контекст живут столько же, сколько обработка пачки.
        await using var scope = _scopeFactory.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<MayakDbContext>();
        var handlers = BuildHandlerMap(scope.ServiceProvider.GetServices<IOutboxHandler>());

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var batch = await LockPendingBatchAsync(db, transaction, cancellationToken);
        var processed = 0;

        foreach (var message in batch)
        {
            processed += await ProcessMessageAsync(
                db,
                transaction,
                handlers,
                message,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return processed;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollInterval = _options.PollInterval > TimeSpan.Zero
            ? _options.PollInterval
            : DefaultPollInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Падение БД/неожиданная ошибка не должны останавливать фоновый сервис.
                _logger.LogError(
                    exception,
                    "Outbox: ошибка диспетчера destination '{Destination}'.",
                    _options.Destination);
            }

            try
            {
                await Task.Delay(pollInterval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Захватывает пачку сообщений; строки остаются заблокированными до конца транзакции.</summary>
    private async Task<List<PendingMessage>> LockPendingBatchAsync(
        MayakDbContext db,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText =
            """
            select id, type, payload::text, attempts
            from outbox_messages
            where destination = @destination
              and processed_at is null
              and available_at <= @now
              and attempts < @max_attempts
            order by available_at, id
            limit @batch_size
            for update skip locked
            """;

        AddParameter(command, "destination", _options.Destination);
        AddParameter(command, "now", _timeProvider.GetUtcNow());
        AddParameter(command, "max_attempts", _options.MaxAttempts);
        AddParameter(command, "batch_size", _options.BatchSize);

        var messages = new List<PendingMessage>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new PendingMessage(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3)));
        }

        return messages;
    }

    /// <summary>
    /// Обрабатывает одно сообщение. Обработчик вызывается под savepoint: частичный результат
    /// упавшего обработчика откатывается, а транзакция пачки остаётся рабочей.
    /// </summary>
    private async Task<int> ProcessMessageAsync(
        MayakDbContext db,
        IDbContextTransaction transaction,
        IReadOnlyDictionary<string, IOutboxHandler> handlers,
        PendingMessage message,
        CancellationToken cancellationToken)
    {
        if (!handlers.TryGetValue(message.Type, out var handler))
        {
            await RecordFailureAsync(
                db,
                transaction,
                message,
                $"Нет обработчика для типа '{message.Type}'.",
                cancellationToken);

            _logger.LogWarning(
                "Outbox: нет обработчика для типа '{Type}' (сообщение {MessageId}).",
                message.Type,
                message.Id);

            return 0;
        }

        await ExecuteAsync(
            db,
            transaction,
            "savepoint outbox_message",
            [],
            cancellationToken);

        try
        {
            using var document = JsonDocument.Parse(message.Payload);

            await handler.HandleAsync(document.RootElement.Clone(), message.Id, cancellationToken);

            await ExecuteAsync(
                db,
                transaction,
                "release savepoint outbox_message",
                [],
                cancellationToken);

            await ExecuteAsync(
                db,
                transaction,
                """
                update outbox_messages
                set processed_at = @now
                where id = @id
                """,
                [("now", _timeProvider.GetUtcNow()), ("id", message.Id)],
                cancellationToken);

            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await ExecuteAsync(
                db,
                transaction,
                "rollback to savepoint outbox_message",
                [],
                cancellationToken);

            // Изменения упавшего обработчика не должны утечь в следующее сообщение пачки.
            db.ChangeTracker.Clear();

            await RecordFailureAsync(db, transaction, message, Describe(exception), cancellationToken);

            _logger.LogWarning(
                exception,
                "Outbox: обработчик типа '{Type}' не обработал сообщение {MessageId}.",
                message.Type,
                message.Id);

            return 0;
        }
    }

    /// <summary>
    /// Увеличивает <c>attempts</c>, сохраняет текст ошибки и откладывает повтор по экспоненте:
    /// <c>now + min(2^attempts, MaxBackoff)</c>. Сообщение остаётся необработанным.
    /// </summary>
    private async Task RecordFailureAsync(
        MayakDbContext db,
        IDbContextTransaction transaction,
        PendingMessage message,
        string error,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var attempts = message.Attempts + 1;

        await ExecuteAsync(
            db,
            transaction,
            """
            update outbox_messages
            set attempts = @attempts,
                last_error = @last_error,
                available_at = @available_at
            where id = @id
            """,
            [
                ("attempts", attempts),
                ("last_error", error),
                ("available_at", now + Backoff(attempts)),
                ("id", message.Id),
            ],
            cancellationToken);
    }

    private TimeSpan Backoff(int attempts)
    {
        var seconds = Math.Pow(2, attempts);

        return TimeSpan.FromSeconds(Math.Min(seconds, _options.MaxBackoff.TotalSeconds));
    }

    /// <summary>Первый обработчик выигрывает: дубли типа — ошибка конфигурации, а не падение.</summary>
    private static Dictionary<string, IOutboxHandler> BuildHandlerMap(IEnumerable<IOutboxHandler> handlers)
    {
        var map = new Dictionary<string, IOutboxHandler>(StringComparer.Ordinal);

        foreach (var handler in handlers)
        {
            map.TryAdd(handler.Type, handler);
        }

        return map;
    }

    private static string Describe(Exception exception) =>
        $"{exception.GetType().Name}: {exception.Message}";

    private static async Task ExecuteAsync(
        MayakDbContext db,
        IDbContextTransaction transaction,
        string sql,
        IReadOnlyList<(string Name, object Value)> parameters,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            AddParameter(command, name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>Сообщение, захваченное под блокировку.</summary>
    private sealed record PendingMessage(Guid Id, string Type, string Payload, int Attempts);
}
