using System.Text.Json;

using BuildingBlocks.Messaging;
using BuildingBlocks.Security;

using Ingestion.Abstractions;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Sources.Contracts;

namespace Api.IntegrationTests.Ingestion;

/// <summary>
/// Worker: горячее применение настроек источников и LLM
///), идемпотентность сообщений, изоляция падений адаптеров и формат outbox-сообщений для Api.
/// </summary>
/// <remarks>
/// Worker-сервисы поднимаются в собственном <see cref="IServiceProvider"/> на настоящей БД
/// <see cref="Infrastructure.PostgresFixture"/>; адаптеры и LLM подменены fake-реализациями,
/// опрос настроек — 20 мс. Изоляция тестов — уникальные источники и очистка данных предыдущих
/// прогонов (коллекция выполняется последовательно).
/// </remarks>
[Collection("postgres")]
public sealed class WorkerHotReloadTests(Infrastructure.PostgresFixture fixture) : IAsyncLifetime
{
    private const string MessageText = "Вода есть, подача идёт";
    private static readonly DateTimeOffset PostedAt = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private string ConnectionString => fixture.ConnectionString;

    public async Task InitializeAsync() =>
        await WorkerDatabase.CleanupPreviousRunsAsync(ConnectionString);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Disabling_source_stops_adapter_without_restart()
    {
        var ct = CancellationToken.None;
        var source = await WorkerDatabase.CreateSourceAsync(ConnectionString, ct: ct);
        var factory = new FakeAdapterFactory();

        await using var worker = new WorkerTestContext(ConnectionString, factory, new FakeLlmProvider());
        await worker.StartAsync(ct);

        var adapter = await WaitForRunningAdapterAsync(factory, source, ct);

        await WorkerDatabase.DisableAsync(ConnectionString, source.Id, ct);

        await WorkerTestContext.WaitUntilAsync(
            () => Task.FromResult(adapter.StopCalls >= 1),
            "адаптер выключенного источника остановлен");

        worker.Supervisor.RunningSourceIds.ShouldNotContain(source.Id);
        factory.InstanceCount(source.Id).ShouldBe(1, "выключение источника не перезапускает адаптер");
    }

    [Fact]
    public async Task Site_source_is_not_read_by_worker()
    {
        var ct = CancellationToken.None;
        var active = await WorkerDatabase.CreateSourceAsync(ConnectionString, ct: ct);
        var siteId = await WorkerDatabase.CreateSiteSourceAsync(ConnectionString, ct);
        var factory = new FakeAdapterFactory();

        await using var worker = new WorkerTestContext(ConnectionString, factory, new FakeLlmProvider());
        await worker.StartAsync(ct);

        // Ждём применения конфигурации по обычному источнику, затем проверяем «Сайт».
        await WaitForRunningAdapterAsync(factory, active, ct);

        worker.Supervisor.RunningSourceIds.ShouldNotContain(siteId);
        factory.InstanceCount(siteId).ShouldBe(0, "у «Сайта» нет адаптера и сообщений");
    }

    [Fact]
    public async Task Changing_prompt_applies_to_next_message()
    {
        var ct = CancellationToken.None;
        var source = await WorkerDatabase.CreateSourceAsync(ConnectionString, ct: ct);
        var factory = new FakeAdapterFactory();
        var llm = new FakeLlmProvider();

        await using var worker = new WorkerTestContext(ConnectionString, factory, llm);
        await worker.StartAsync(ct);
        await WaitForRunningAdapterAsync(factory, source, ct);

        var newPrompt = $"НОВЫЙ ПРОМПТ {Guid.NewGuid():N}";
        await WorkerDatabase.SetPromptAsync(ConnectionString, source.Id, newPrompt, ct);

        await WaitForRestartAsync(factory, source, ct);

        factory.Latest(source.Id)!.Emit(Message(source, "prompt-1"));

        await WorkerTestContext.WaitUntilAsync(
            () => Task.FromResult(llm.RequestCount >= 1),
            "LLM получил сообщение после смены промпта");

        llm.Requests[^1].User.ShouldContain(newPrompt);
    }

    [Fact]
    public async Task Changing_statuses_applies_to_next_message()
    {
        var ct = CancellationToken.None;
        var source = await WorkerDatabase.CreateSourceAsync(ConnectionString, ct: ct);
        var factory = new FakeAdapterFactory();
        var llm = new FakeLlmProvider();

        await using var worker = new WorkerTestContext(ConnectionString, factory, llm);
        await worker.StartAsync(ct);
        await WaitForRunningAdapterAsync(factory, source, ct);

        var suffix = Guid.NewGuid().ToString("N");
        var statusName = $"Вода есть {suffix}";
        var statusDescription = $"Новое описание статуса {suffix}";
        await WorkerDatabase.SetStatusAsync(
            ConnectionString,
            source.Id,
            "ok",
            statusName,
            statusDescription,
            ct);

        await WaitForRestartAsync(factory, source, ct);

        factory.Latest(source.Id)!.Emit(Message(source, "status-1"));

        await WorkerTestContext.WaitUntilAsync(
            () => Task.FromResult(llm.RequestCount >= 1),
            "LLM получил сообщение после смены статусов");

        llm.Requests[^1].System.ShouldContain(statusName);
        llm.Requests[^1].System.ShouldContain(statusDescription);
    }

    [Fact]
    public async Task Changing_llm_key_applies_without_restart()
    {
        var ct = CancellationToken.None;
        var source = await WorkerDatabase.CreateSourceAsync(ConnectionString, ct: ct);
        var factory = new FakeAdapterFactory();
        var llm = new FakeLlmProvider();

        await using var worker = new WorkerTestContext(ConnectionString, factory, llm);

        var cipher = worker.Services.GetRequiredService<ISecretCipher>();
        await WorkerDatabase.SetLlmSecretAsync(ConnectionString, cipher, "key-first", ct);

        llm.ApiKeyProbe = async token =>
        {
            await using var scope = worker.Services.CreateAsyncScope();
            var reader = scope.ServiceProvider.GetRequiredService<ISecretReader>();

            return (await reader.ReadLlmAsync(token))?.ApiKey;
        };

        await worker.StartAsync(ct);
        var adapter = await WaitForRunningAdapterAsync(factory, source, ct);

        adapter.Emit(Message(source, "llm-1"));
        await WorkerTestContext.WaitUntilAsync(
            () => Task.FromResult(llm.ObservedApiKeys.Count == 1),
            "первый ключ LLM прочитан");

        // Смена ключа в админке: без перезапуска Worker-а и без перезапуска адаптера.
        await WorkerDatabase.SetLlmSecretAsync(ConnectionString, cipher, "key-second", ct);

        adapter.Emit(Message(source, "llm-2"));
        await WorkerTestContext.WaitUntilAsync(
            () => Task.FromResult(llm.ObservedApiKeys.Count == 2),
            "второй ключ LLM прочитан");

        llm.ObservedApiKeys[0].ShouldBe("key-first");
        llm.ObservedApiKeys[1].ShouldBe("key-second");
        factory.InstanceCount(source.Id).ShouldBe(1, "смена ключа LLM не перезапускает источник");
    }

    [Fact]
    public async Task Changing_one_source_does_not_restart_others()
    {
        var ct = CancellationToken.None;
        var first = await WorkerDatabase.CreateSourceAsync(ConnectionString, ct: ct);
        var second = await WorkerDatabase.CreateSourceAsync(ConnectionString, ct: ct);
        var factory = new FakeAdapterFactory();
        var llm = new FakeLlmProvider();

        await using var worker = new WorkerTestContext(ConnectionString, factory, llm);
        await worker.StartAsync(ct);

        await WaitForRunningAdapterAsync(factory, first, ct);
        await WaitForRunningAdapterAsync(factory, second, ct);

        var secondAdapter = factory.Latest(second.Id)!;

        await WorkerDatabase.SetPromptAsync(
            ConnectionString,
            first.Id,
            $"Промпт только первого {Guid.NewGuid():N}",
            ct);

        await WaitForRestartAsync(factory, first, ct);

        factory.InstanceCount(second.Id).ShouldBe(1, "второй источник не перезапускается");
        secondAdapter.StartCalls.ShouldBe(1);
        worker.Supervisor.RunningSourceIds.ShouldContain(second.Id);
    }

    [Fact]
    public async Task Duplicate_message_external_id_processed_once()
    {
        var ct = CancellationToken.None;
        var source = await WorkerDatabase.CreateSourceAsync(ConnectionString, ct: ct);
        var factory = new FakeAdapterFactory();
        var llm = new FakeLlmProvider();

        await using var worker = new WorkerTestContext(ConnectionString, factory, llm);
        await worker.StartAsync(ct);
        var adapter = await WaitForRunningAdapterAsync(factory, source, ct);

        var message = Message(source, "duplicate-1");
        adapter.Emit(message);
        adapter.Emit(message);

        await WorkerTestContext.WaitUntilAsync(
            async () => (await WorkerDatabase.ReadOutboxAsync(ConnectionString, source.Id, ct)).Count == 1,
            "первое сообщение обработано");

        // Даём второму сообщению время пройти (или не пройти) обработку.
        await Task.Delay(300, ct);

        (await WorkerDatabase.CountAsync(
            ConnectionString,
            "select count(*) from raw_messages where source_id = @id and external_id = @external_id",
            ct,
            ("id", source.Id),
            ("external_id", "duplicate-1"))).ShouldBe(1L);

        llm.RequestCount.ShouldBe(1, "повтор external_id не должен запускать pipeline");
    }

    [Fact]
    public async Task Crash_in_one_adapter_does_not_stop_others()
    {
        var ct = CancellationToken.None;
        var broken = await WorkerDatabase.CreateSourceAsync(ConnectionString, ct: ct);
        var healthy = await WorkerDatabase.CreateSourceAsync(ConnectionString, ct: ct);
        var factory = new FakeAdapterFactory();
        var llm = new FakeLlmProvider();

        await using var worker = new WorkerTestContext(ConnectionString, factory, llm);
        await worker.StartAsync(ct);

        var brokenAdapter = await WaitForRunningAdapterAsync(factory, broken, ct);
        await WaitForRunningAdapterAsync(factory, healthy, ct);

        brokenAdapter.Crash(new InvalidOperationException("адаптер упал"));

        await WaitForRestartAsync(factory, broken, ct);

        var healthyAdapter = factory.Latest(healthy.Id)!;
        healthyAdapter.Emit(Message(healthy, "healthy-1"));

        await WorkerTestContext.WaitUntilAsync(
            async () => (await WorkerDatabase.ReadOutboxAsync(ConnectionString, healthy.Id, ct)).Count == 1,
            "второй источник обработал сообщение после падения первого");

        factory.InstanceCount(healthy.Id).ShouldBe(1, "падение одного адаптера не перезапускает другие");
    }

    [Fact]
    public async Task Message_is_saved_with_outbox_in_one_transaction()
    {
        var ct = CancellationToken.None;
        var source = await WorkerDatabase.CreateSourceAsync(ConnectionString, ct: ct);
        var factory = new FakeAdapterFactory();
        var llm = new FakeLlmProvider();

        await using var worker = new WorkerTestContext(
            ConnectionString,
            factory,
            llm,
            services => services.AddScoped<IOutbox, ThrowingOutbox>());

        await worker.StartAsync(ct);
        var adapter = await WaitForRunningAdapterAsync(factory, source, ct);

        adapter.Emit(Message(source, "rollback-1"));

        await WorkerTestContext.WaitUntilAsync(
            () => Task.FromResult(llm.RequestCount >= 1),
            "pipeline дошёл до записи в outbox");

        await Task.Delay(300, ct);

        (await WorkerDatabase.CountAsync(
            ConnectionString,
            "select count(*) from raw_messages where source_id = @id and external_id = @external_id",
            ct,
            ("id", source.Id),
            ("external_id", "rollback-1"))).ShouldBe(0L);

        (await WorkerDatabase.CountAsync(
            ConnectionString,
            """
            select count(*) from outbox_messages
            where destination = 'api' and payload->>'sourceId' = @source_id
            """,
            ct,
            ("source_id", source.Id.ToString()))).ShouldBe(0L);
    }

    [Fact]
    public async Task Unknown_adapter_type_reports_error_without_crash()
    {
        var ct = CancellationToken.None;

        // max_chat есть в source_types, но фабрики адаптера для него пока нет.
        var unsupported = await WorkerDatabase.CreateSourceAsync(
            ConnectionString,
            sourceType: "max_chat",
            ct: ct);
        var supported = await WorkerDatabase.CreateSourceAsync(ConnectionString, ct: ct);

        var factory = new FakeAdapterFactory();
        var llm = new FakeLlmProvider();

        await using var worker = new WorkerTestContext(ConnectionString, factory, llm);
        await worker.StartAsync(ct);

        await WorkerTestContext.WaitUntilAsync(
            async () =>
            {
                var health = await WorkerDatabase.ReadHealthAsync(ConnectionString, unsupported.Id, ct);

                return health.State == "error" &&
                       health.Detail is not null &&
                       health.Detail.Contains("не реализован", StringComparison.Ordinal);
            },
            "источник без фабрики адаптера получил health-ошибку");

        worker.Supervisor.RunningSourceIds.ShouldNotContain(unsupported.Id);

        // Worker продолжает работать: поддерживаемый источник запускается.
        await WaitForRunningAdapterAsync(factory, supported, ct);
    }

    [Fact]
    public async Task Pipeline_decision_maps_to_outbox_payload_format()
    {
        var ct = CancellationToken.None;
        var source = await WorkerDatabase.CreateSourceAsync(ConnectionString, ct: ct);
        var factory = new FakeAdapterFactory();
        var llm = new FakeLlmProvider
        {
            ResponseFactory = request =>
            {
                if (request.Message.Contains("НЕИЗВЕСТНО", StringComparison.Ordinal))
                {
                    return FakeLlmProvider.JsonResponse(true, "other", null, 0.9, "НЕИЗВЕСТНО");
                }

                if (request.Message.Contains("НИЗКАЯ", StringComparison.Ordinal))
                {
                    return FakeLlmProvider.JsonResponse(true, "ok", null, 0.1, "НИЗКАЯ");
                }

                return FakeLlmProvider.JsonResponse(true, "ok", null, 0.9, "Вода есть");
            },
        };

        await using var worker = new WorkerTestContext(ConnectionString, factory, llm);
        await worker.StartAsync(ct);
        var adapter = await WaitForRunningAdapterAsync(factory, source, ct);

        adapter.Emit(Message(source, "format-propose"));
        adapter.Emit(Message(source, "format-moderate", "НЕИЗВЕСТНО состояние"));
        adapter.Emit(Message(source, "format-low", "НИЗКАЯ уверенность"));

        await WorkerTestContext.WaitUntilAsync(
            async () => (await WorkerDatabase.ReadOutboxAsync(ConnectionString, source.Id, ct)).Count == 3,
            "все решения pipeline записаны в outbox");

        var rows = await WorkerDatabase.ReadOutboxAsync(ConnectionString, source.Id, ct);

        var observationRow = rows.Single(row => row.Type == "ObservationProposed");
        using (var document = JsonDocument.Parse(observationRow.Payload))
        {
            var payload = document.RootElement;

            payload.EnumerateObject().Select(property => property.Name).ShouldBe(
                ["sourceId", "mapObjectId", "statusCode", "rawMessageId", "observedAt", "confidence"],
                ignoreOrder: true);

            payload.GetProperty("sourceId").GetGuid().ShouldBe(source.Id);
            payload.GetProperty("mapObjectId").GetGuid().ShouldBe(source.ObjectId);
            payload.GetProperty("statusCode").GetString().ShouldBe("ok");

            var rawMessageId = await WorkerDatabase.ReadRawMessageIdAsync(
                ConnectionString,
                source.Id,
                "format-propose",
                ct);
            rawMessageId.ShouldNotBeNull("raw_messages должен содержать исходное сообщение");
            payload.GetProperty("rawMessageId").GetGuid().ShouldBe(rawMessageId!.Value);

            var observedAt = DateTimeOffset.Parse(
                payload.GetProperty("observedAt").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture);
            observedAt.ShouldBe(PostedAt);

            payload.GetProperty("confidence").GetDouble().ShouldBe(0.9, 0.0001);
        }

        var moderationRows = rows.Where(row => row.Type == "ModerationCaseRequested").ToList();
        moderationRows.Count.ShouldBe(2);

        var kinds = moderationRows
            .Select(row =>
            {
                using var document = JsonDocument.Parse(row.Payload);

                return document.RootElement.GetProperty("kind").GetString();
            })
            .ToList();

        kinds.ShouldContain("invalid_status");
        kinds.ShouldContain("low_confidence");

        var moderationRow = moderationRows.Single(row =>
            row.Payload.Contains("\"invalid_status\"", StringComparison.Ordinal));
        using (var document = JsonDocument.Parse(moderationRow.Payload))
        {
            var payload = document.RootElement;

            payload.EnumerateObject().Select(property => property.Name).ShouldBe(
                [
                    "sourceId",
                    "rawMessageId",
                    "kind",
                    "reason",
                    "proposedStatusCode",
                    "proposedMapObjectId",
                    "confidence",
                    "evidenceQuote",
                ],
                ignoreOrder: true);

            payload.GetProperty("sourceId").GetGuid().ShouldBe(source.Id);
            payload.GetProperty("kind").GetString().ShouldBe("invalid_status");
            payload.GetProperty("reason").GetString().ShouldNotBeNullOrWhiteSpace();
            payload.GetProperty("proposedStatusCode").GetString().ShouldBe("other");
            payload.GetProperty("proposedMapObjectId").ValueKind.ShouldBe(JsonValueKind.Null);
            payload.GetProperty("confidence").GetDouble().ShouldBe(0.9, 0.0001);
            payload.GetProperty("evidenceQuote").GetString().ShouldBe("НЕИЗВЕСТНО");
        }
    }

    private static RawMessage Message(TestSource source, string externalId, string text = MessageText) =>
        new(source.Id, externalId, text, "житель", source.ChatId, null, PostedAt);

    private static async Task<FakeDataSource> WaitForRunningAdapterAsync(
        FakeAdapterFactory factory,
        TestSource source,
        CancellationToken ct)
    {
        await WorkerTestContext.WaitUntilAsync(
            () => Task.FromResult(factory.InstanceCount(source.Id) == 1),
            $"адаптер источника {source.Id} создан");

        var adapter = factory.Latest(source.Id)!;

        await WorkerTestContext.WaitUntilAsync(
            () => Task.FromResult(adapter.StartCalls == 1),
            $"адаптер источника {source.Id} запущен");

        ct.ThrowIfCancellationRequested();

        return adapter;
    }

    private static Task WaitForRestartAsync(
        FakeAdapterFactory factory,
        TestSource source,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return WorkerTestContext.WaitUntilAsync(
            () => Task.FromResult(factory.InstanceCount(source.Id) >= 2),
            $"адаптер источника {source.Id} перезапущен после смены настроек");
    }

    /// <summary>Outbox, который всегда падает: проверяет откат сохранения raw_messages.</summary>
    private sealed class ThrowingOutbox : IOutbox
    {
        public void Enqueue(string destination, string type, object payload) =>
            throw new InvalidOperationException("Outbox недоступен.");
    }
}
