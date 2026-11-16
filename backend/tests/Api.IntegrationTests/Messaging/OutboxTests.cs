using System.Collections.Concurrent;
using System.Text.Json;

using Api.IntegrationTests.Infrastructure;

using BuildingBlocks.Messaging;
using BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Shouldly;

namespace Api.IntegrationTests.Messaging;

/// <summary>
/// Transactional outbox: запись события в транзакции бизнес-данных,
/// доставка по порядку <c>available_at</c>, повторы с экспоненциальной задержкой, «отравленное»
/// сообщение, конкуренция двух диспетчеров через <c>FOR UPDATE SKIP LOCKED</c>, изоляция
/// destination и идемпотентность по <c>outbox_messages.id</c>.
/// </summary>
/// <remarks>
/// БД общая на всю коллекцию. Check-ограничение схемы разрешает только destination
/// <c>api</c>/<c>worker</c> (db/schema.sql), поэтому изоляция тестов — очистка
/// <c>outbox_messages</c> перед каждым тестом (тесты коллекции выполняются последовательно),
/// а не произвольные уникальные destination.
/// </remarks>
[Collection("postgres")]
public sealed class OutboxTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string ApiDestination = "api";
    private const string WorkerDestination = "worker";

    private static readonly DateTimeOffset Start = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => ResetOutboxAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Enqueue_is_rolled_back_with_business_transaction()
    {
        var ct = CancellationToken.None;
        var type = UniqueType("rollback");
        await using var provider = BuildProvider(ApiDestination, TimeProvider.System);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MayakDbContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();

            // То же SaveChanges, что и у бизнес-данных: откат транзакции отменяет и событие.
            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                outbox.Enqueue(ApiDestination, type, new { name = "rolled-back" });
                await db.SaveChangesAsync(ct);
                await transaction.RollbackAsync(ct);
            }

            (await ReadRowsAsync(type, ct)).ShouldBeEmpty();

            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                outbox.Enqueue(ApiDestination, type, new { name = "committed" });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
        }

        var committed = await ReadRowsAsync(type, ct);
        committed.Count.ShouldBe(1);
        committed[0].Payload.ShouldContain("committed");
        committed[0].ProcessedAt.ShouldBeNull();
        committed[0].Attempts.ShouldBe(0);
    }

    [Fact]
    public async Task Dispatcher_delivers_in_order_of_available_at()
    {
        var ct = CancellationToken.None;
        var clock = new ManualTimeProvider(Start);
        var state = new TestHandlerState { Type = UniqueType("order") };
        await using var provider = BuildProvider(
            ApiDestination,
            clock,
            services =>
            {
                services.AddSingleton(state);
                services.AddOutboxHandler<ScriptedOutboxHandler>();
            });

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MayakDbContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();

            clock.Set(Start);
            outbox.Enqueue(ApiDestination, state.Type, new { name = "first" });
            clock.Set(Start.AddSeconds(5));
            outbox.Enqueue(ApiDestination, state.Type, new { name = "third" });
            clock.Set(Start.AddSeconds(2));
            outbox.Enqueue(ApiDestination, state.Type, new { name = "second" });

            await db.SaveChangesAsync(ct);
        }

        clock.Set(Start.AddSeconds(10));

        var processed = await DispatchOnceAsync(provider, ct);

        processed.ShouldBe(3);
        state.Snapshot().Select(delivery => delivery.Name).ShouldBe(["first", "second", "third"]);
    }

    [Fact]
    public async Task Failed_handler_retries_with_backoff()
    {
        var ct = CancellationToken.None;
        var clock = new ManualTimeProvider(Start);
        var state = new TestHandlerState
        {
            Type = UniqueType("retry"),
            FailWhen = _ => "handler failed",
        };
        await using var provider = BuildProvider(
            ApiDestination,
            clock,
            services =>
            {
                services.AddSingleton(state);
                services.AddOutboxHandler<ScriptedOutboxHandler>();
            });

        await EnqueueAsync(provider, ApiDestination, state.Type, "retry", ct);

        (await DispatchOnceAsync(provider, ct)).ShouldBe(0);

        var row = (await ReadRowsAsync(state.Type, ct)).ShouldHaveSingleItem();
        row.Attempts.ShouldBe(1);
        row.ProcessedAt.ShouldBeNull();
        row.LastError.ShouldNotBeNull();
        row.LastError!.ShouldContain("handler failed");
        // Первый повтор: 2^1 = 2 секунды.
        row.AvailableAt.ShouldBe(Start.AddSeconds(2));
        state.Count.ShouldBe(1);

        // Раньше available_at сообщение не выбирается — повтора нет.
        clock.Set(Start.AddSeconds(1));
        (await DispatchOnceAsync(provider, ct)).ShouldBe(0);
        state.Count.ShouldBe(1);

        clock.Set(Start.AddSeconds(2));
        (await DispatchOnceAsync(provider, ct)).ShouldBe(0);

        row = (await ReadRowsAsync(state.Type, ct)).ShouldHaveSingleItem();
        row.Attempts.ShouldBe(2);
        row.ProcessedAt.ShouldBeNull();
        // Второй повтор: 2^2 = 4 секунды от момента ошибки.
        row.AvailableAt.ShouldBe(Start.AddSeconds(2).AddSeconds(4));
        state.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Poison_message_does_not_block_queue()
    {
        var ct = CancellationToken.None;
        var clock = new ManualTimeProvider(Start);
        var state = new TestHandlerState
        {
            Type = UniqueType("poison"),
            FailWhen = payload => payload.TryGetProperty("poison", out var poison)
                && poison.ValueKind == JsonValueKind.True
                    ? "poison message"
                    : null,
        };
        await using var provider = BuildProvider(
            ApiDestination,
            clock,
            services =>
            {
                services.AddSingleton(state);
                services.AddOutboxHandler<ScriptedOutboxHandler>();
            },
            options => options.MaxAttempts = 2);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MayakDbContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();

            // Poison раньше по available_at: падение первого не должно мешать второму.
            clock.Set(Start);
            outbox.Enqueue(ApiDestination, state.Type, new { name = "poison", poison = true });
            clock.Set(Start.AddMilliseconds(1));
            outbox.Enqueue(ApiDestination, state.Type, new { name = "good", poison = false });

            await db.SaveChangesAsync(ct);
        }

        clock.Set(Start.AddSeconds(1));

        (await DispatchOnceAsync(provider, ct)).ShouldBe(1);
        state.Snapshot().Select(delivery => delivery.Name).ShouldBe(["poison", "good"]);

        // Вторая (последняя) попытка poison.
        clock.Set(Start.AddSeconds(3));
        (await DispatchOnceAsync(provider, ct)).ShouldBe(0);
        state.Count.ShouldBe(3);

        // attempts = MaxAttempts: сообщение исключено из выборки и не блокирует очередь.
        clock.Set(Start.AddSeconds(7));
        (await DispatchOnceAsync(provider, ct)).ShouldBe(0);
        state.Count.ShouldBe(3);

        var rows = await ReadRowsAsync(state.Type, ct);
        var poisonRow = rows.Single(row => PayloadName(row) == "poison");
        poisonRow.Attempts.ShouldBe(2);
        poisonRow.ProcessedAt.ShouldBeNull();
        poisonRow.LastError!.ShouldContain("poison message");

        var goodRow = rows.Single(row => PayloadName(row) == "good");
        goodRow.ProcessedAt.ShouldNotBeNull();
        goodRow.Attempts.ShouldBe(0);
    }

    [Fact]
    public async Task Two_dispatchers_do_not_process_same_message()
    {
        var ct = CancellationToken.None;
        var clock = new ManualTimeProvider(Start);
        var state = new TestHandlerState { Type = UniqueType("concurrent") };

        void Configure(IServiceCollection services)
        {
            services.AddSingleton(state);
            services.AddOutboxHandler<ScriptedOutboxHandler>();
        }

        await using var first = BuildProvider(ApiDestination, clock, Configure);
        await using var second = BuildProvider(ApiDestination, clock, Configure);

        const int messageCount = 20;

        await using (var scope = first.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MayakDbContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();

            for (var index = 0; index < messageCount; index++)
            {
                outbox.Enqueue(ApiDestination, state.Type, new { name = $"message-{index}" });
            }

            await db.SaveChangesAsync(ct);
        }

        var firstDispatcher = first.GetRequiredService<OutboxDispatcher>();
        var secondDispatcher = second.GetRequiredService<OutboxDispatcher>();

        await Task.WhenAll(
            firstDispatcher.DispatchOnceAsync(ct),
            secondDispatcher.DispatchOnceAsync(ct));

        var rows = await ReadRowsAsync(state.Type, ct);
        rows.Count.ShouldBe(messageCount);
        rows.ShouldAllBe(row => row.ProcessedAt != null);

        var deliveredIds = state.Snapshot().Select(delivery => delivery.MessageId).ToList();
        deliveredIds.Count.ShouldBe(messageCount);
        // SKIP LOCKED: ни одно сообщение не доставлено дважды.
        deliveredIds.Distinct().Count().ShouldBe(messageCount);
    }

    [Fact]
    public async Task Message_for_other_destination_is_ignored()
    {
        var ct = CancellationToken.None;
        var state = new TestHandlerState { Type = UniqueType("routing") };
        await using var provider = BuildProvider(
            ApiDestination,
            TimeProvider.System,
            services =>
            {
                services.AddSingleton(state);
                services.AddOutboxHandler<ScriptedOutboxHandler>();
            });

        await EnqueueAsync(provider, WorkerDestination, state.Type, "for-worker", ct);
        await EnqueueAsync(provider, ApiDestination, state.Type, "for-api", ct);

        (await DispatchOnceAsync(provider, ct)).ShouldBe(1);

        state.Snapshot().ShouldHaveSingleItem().Name.ShouldBe("for-api");

        var rows = await ReadRowsAsync(state.Type, ct);
        var workerRow = rows.Single(row => row.Destination == WorkerDestination);
        workerRow.ProcessedAt.ShouldBeNull();
        workerRow.Attempts.ShouldBe(0);

        var apiRow = rows.Single(row => row.Destination == ApiDestination);
        apiRow.ProcessedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Missing_handler_marks_error_not_crash()
    {
        var ct = CancellationToken.None;
        var clock = new ManualTimeProvider(Start);
        var type = UniqueType("missing-handler");
        await using var provider = BuildProvider(ApiDestination, clock);

        await EnqueueAsync(provider, ApiDestination, type, "no-handler", ct);

        // Отсутствие обработчика — ошибка сообщения, а не падение диспетчера.
        var processed = await DispatchOnceAsync(provider, ct);

        processed.ShouldBe(0);

        var row = (await ReadRowsAsync(type, ct)).ShouldHaveSingleItem();
        row.Attempts.ShouldBe(1);
        row.ProcessedAt.ShouldBeNull();
        row.LastError.ShouldNotBeNull();
        row.LastError!.ShouldContain(type);
        row.AvailableAt.ShouldBe(Start.AddSeconds(2));
    }

    [Fact]
    public async Task Handler_receives_message_id_for_idempotency()
    {
        var ct = CancellationToken.None;
        var state = new TestHandlerState { Type = UniqueType("idempotency") };
        await using var provider = BuildProvider(
            ApiDestination,
            TimeProvider.System,
            services =>
            {
                services.AddSingleton(state);
                services.AddOutboxHandler<ScriptedOutboxHandler>();
            });

        await EnqueueAsync(provider, ApiDestination, state.Type, "identify", ct);

        var row = (await ReadRowsAsync(state.Type, ct)).ShouldHaveSingleItem();

        (await DispatchOnceAsync(provider, ct)).ShouldBe(1);

        var delivery = state.Snapshot().ShouldHaveSingleItem();
        delivery.MessageId.ShouldBe(row.Id);
        delivery.Name.ShouldBe("identify");

        // Повторный опрос не доставляет обработанное сообщение.
        (await DispatchOnceAsync(provider, ct)).ShouldBe(0);
        state.Count.ShouldBe(1);
    }

    private static Task<int> DispatchOnceAsync(IServiceProvider provider, CancellationToken ct) =>
        provider.GetRequiredService<OutboxDispatcher>().DispatchOnceAsync(ct);

    private static async Task EnqueueAsync(
        IServiceProvider provider,
        string destination,
        string type,
        string name,
        CancellationToken ct)
    {
        await using var scope = provider.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IOutbox>()
            .Enqueue(destination, type, new { name });

        await scope.ServiceProvider.GetRequiredService<MayakDbContext>().SaveChangesAsync(ct);
    }

    private ServiceProvider BuildProvider(
        string destination,
        TimeProvider timeProvider,
        Action<IServiceCollection>? configureServices = null,
        Action<OutboxDispatcherOptions>? configureDispatcher = null)
    {
        var services = new ServiceCollection();

        services.AddPersistence(fixture.ConnectionString);
        services.AddSingleton(timeProvider);
        services.AddOutbox();
        services.AddOutboxDispatcher(destination, configureDispatcher);

        configureServices?.Invoke(services);

        return services.BuildServiceProvider();
    }

    private async Task<List<OutboxRow>> ReadRowsAsync(string type, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select id, destination, type, payload::text, available_at, processed_at, attempts, last_error
            from outbox_messages
            where type = @type
            order by available_at, id
            """,
            connection);
        command.Parameters.AddWithValue("type", type);

        var rows = new List<OutboxRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new OutboxRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                ReadTimestamp(reader, 4),
                reader.IsDBNull(5) ? null : ReadTimestamp(reader, 5),
                reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return rows;
    }

    private static DateTimeOffset ReadTimestamp(NpgsqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));

    private static string? PayloadName(OutboxRow row)
    {
        using var document = JsonDocument.Parse(row.Payload);

        return document.RootElement.TryGetProperty("name", out var name) ? name.GetString() : null;
    }

    private async Task ResetOutboxAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("truncate table outbox_messages;", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string UniqueType(string prefix) => $"test.{prefix}.{Guid.NewGuid():N}";

    private sealed record OutboxRow(
        Guid Id,
        string Destination,
        string Type,
        string Payload,
        DateTimeOffset AvailableAt,
        DateTimeOffset? ProcessedAt,
        int Attempts,
        string? LastError);
}

/// <summary>Управляемые часы: <see cref="OutboxDispatcher"/> берёт время только отсюда.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Set(DateTimeOffset value) => _now = value;
}

/// <summary>Доставка обработчику: идентификатор сообщения — ключ идемпотентности.</summary>
internal sealed record Delivery(Guid MessageId, string? Name);

/// <summary>Общий для провайдеров журнал доставок и сценарий отказа тестового обработчика.</summary>
internal sealed class TestHandlerState
{
    public required string Type { get; init; }

    public ConcurrentQueue<Delivery> Deliveries { get; } = new();

    /// <summary>Возвращает текст ошибки, чтобы обработчик упал, или <c>null</c> для успеха.</summary>
    public Func<JsonElement, string?>? FailWhen { get; set; }

    public int Count => Deliveries.Count;

    public IReadOnlyList<Delivery> Snapshot() => [.. Deliveries];
}

/// <summary>Обработчик, который пишет доставки в общий журнал и может падать по сценарию.</summary>
internal sealed class ScriptedOutboxHandler(TestHandlerState state) : IOutboxHandler
{
    public string Type => state.Type;

    public Task HandleAsync(JsonElement payload, Guid messageId, CancellationToken cancellationToken)
    {
        var name = payload.TryGetProperty("name", out var nameElement)
            && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;

        state.Deliveries.Enqueue(new Delivery(messageId, name));

        var error = state.FailWhen?.Invoke(payload);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        return Task.CompletedTask;
    }
}
