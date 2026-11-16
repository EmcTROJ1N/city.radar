using System.Globalization;
using System.Net;
using System.Net.Sockets;

using Ingestion.Abstractions;
using Ingestion.Adapters.TelegramBotApi;
using Ingestion.Adapters.TelegramMtProto;

using Shouldly;

namespace Api.IntegrationTests.Ingestion;

/// <summary>
/// Состояния адаптеров Telegram на подменённом транспорте :
/// FloodWait и возврат к работе, auth_error без цикла переподключений, фильтрация и дедупликация
/// Bot API, отображение обновлений MTProto, backoff сетевой ошибки без прямого соединения и выбор
/// фабрики по режиму источника. Реальный Telegram не используется.
/// </summary>
public sealed class AdapterHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FloodWait_reports_state_and_resumes_after_delay()
    {
        var time = new ManualTimeProvider(Now);
        var recorder = new HealthRecorder();
        var incoming = new MtProtoIncomingMessage(
            101,
            "Вода есть, подача идёт",
            "житель",
            "Вода",
            AdapterTestData.ChatUsername,
            Now);

        var factory = new ScriptedTransportFactory(
            call => call == 1
                ? new StubTelegramTransport(
                    open: TelegramTransportScripts.Fail(
                        new TelegramTransportException(
                            TelegramFailureKind.FloodWait,
                            "Telegram просит подождать: FLOOD_WAIT_30",
                            TimeSpan.FromSeconds(30))))
                : new StubTelegramTransport(
                    read: ct => TelegramTransportScripts.YieldThenBlock([incoming], ct)));

        var adapter = new TelegramMtProtoAdapter(
            AdapterTestData.Config(reportHealth: recorder.Callback),
            factory,
            time);

        try
        {
            await adapter.StartAsync(CancellationToken.None);

            await WorkerTestContext.WaitUntilAsync(
                () => Task.FromResult(recorder.Has(SourceState.FloodWait)),
                "FloodWait записан в health");

            var report = recorder.Last(SourceState.FloodWait)!;
            report.Detail.ShouldNotBeNull();

            var until = DateTimeOffset.Parse(
                report.Detail!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
            until.ShouldBe(Now + TimeSpan.FromSeconds(30));

            // Даём циклу зарегистрировать таймер ожидания и двигаем управляемое время:
            // каждый шаг проверяет, не вернулся ли адаптер в ok.
            await WorkerTestContext.WaitUntilAsync(
                () =>
                {
                    time.Advance(TimeSpan.FromSeconds(1));

                    return Task.FromResult(recorder.Has(SourceState.Ok));
                },
                "после FloodWait адаптер вернулся в ok");

            using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var messages = await ReadMessagesAsync(adapter, 1, readTimeout.Token);

            messages.Count.ShouldBe(1);
            messages[0].Text.ShouldBe("Вода есть, подача идёт");

            (await adapter.CheckHealthAsync(CancellationToken.None)).LastMessageAt.ShouldBe(Now);
        }
        finally
        {
            await adapter.DisposeAsync();
        }
    }

    [Fact]
    public async Task Auth_error_reports_auth_error_and_stops_reconnect_loop()
    {
        var time = new ManualTimeProvider(Now);
        var recorder = new HealthRecorder();

        var factory = new ScriptedTransportFactory(
            _ => new StubTelegramTransport(
                open: TelegramTransportScripts.Fail(
                    new TelegramTransportException(
                        TelegramFailureKind.Auth,
                        "Сессия Telegram недействительна (разлогин или бан): SESSION_REVOKED"))));

        var adapter = new TelegramMtProtoAdapter(
            AdapterTestData.Config(reportHealth: recorder.Callback),
            factory,
            time);

        try
        {
            await adapter.StartAsync(CancellationToken.None);

            await WorkerTestContext.WaitUntilAsync(
                () => Task.FromResult(recorder.Has(SourceState.AuthError)),
                "auth_error записан в health");

            recorder.Last(SourceState.AuthError)!.Detail.ShouldNotBeNull()
                .ShouldContain("разлогин");

            // Даже спустя много времени переподключений нет: цикл остановлен до замены сессии.
            time.Advance(TimeSpan.FromMinutes(10));
            await Task.Delay(200);

            factory.CreatedCount.ShouldBe(1);
            (await adapter.CheckHealthAsync(CancellationToken.None)).State
                .ShouldBe(SourceState.AuthError);
        }
        finally
        {
            await adapter.DisposeAsync();
        }
    }

    [Fact]
    public async Task Bot_api_filters_foreign_chats_and_bot_own_messages()
    {
        var recorder = new HealthRecorder();
        var handler = new BotApiHandler(
            call => call == 1
                ? AdapterTestData.GetUpdates(
                    AdapterTestData.TelegramMessage(1, "Сообщение чужого чата", chatId: 999, username: "other"),
                    AdapterTestData.TelegramMessage(2, "Сообщение бота", fromBot: true),
                    AdapterTestData.TelegramMessage(3, "Вода есть"))
                : """{"ok":true,"result":[]}""");

        using var http = new HttpClient(handler);
        var adapter = new TelegramBotApiAdapter(
            AdapterTestData.Config(
                mode: TelegramSourceSettings.BotMode,
                session: AdapterTestData.BotToken,
                reportHealth: recorder.Callback),
            http,
            TimeProvider.System,
            apiBaseUrl: "http://bot.local");

        var received = new List<RawMessage>();
        try
        {
            await adapter.StartAsync(CancellationToken.None);
            var reader = CollectAsync(adapter, received);

            await WorkerTestContext.WaitUntilAsync(
                () => Task.FromResult(received.Count >= 1),
                "сообщение разрешённого чата прочитано");

            await Task.Delay(300);

            received.Count.ShouldBe(1, "сообщения чужих чатов и самого бота не читаются");
            var message = received[0];

            message.Text.ShouldBe("Вода есть");
            message.ExternalId.ShouldBe($"{AdapterTestData.ChatId}:3");
            message.Author.ShouldBe("житель");
            message.Link.ShouldBe($"https://t.me/{AdapterTestData.ChatUsername}/3");
            message.PostedAt.ShouldBe(
                DateTimeOffset.FromUnixTimeSeconds(1763200000).ToUniversalTime());

            await adapter.DisposeAsync();
            await reader;
        }
        finally
        {
            await adapter.DisposeAsync();
        }
    }

    [Fact]
    public async Task Bot_api_dedups_external_id()
    {
        // Одно и то же обновление приходит в каждом ответе getUpdates.
        var handler = new BotApiHandler(
            _ => AdapterTestData.GetUpdates(
                AdapterTestData.TelegramMessage(7, "Вода есть"),
                AdapterTestData.TelegramMessage(7, "Вода есть")));

        using var http = new HttpClient(handler);
        var adapter = new TelegramBotApiAdapter(
            AdapterTestData.Config(
                mode: TelegramSourceSettings.BotMode,
                session: AdapterTestData.BotToken),
            http,
            TimeProvider.System,
            apiBaseUrl: "http://bot.local");

        var received = new List<RawMessage>();
        try
        {
            await adapter.StartAsync(CancellationToken.None);
            var reader = CollectAsync(adapter, received);

            await WorkerTestContext.WaitUntilAsync(
                () => Task.FromResult(handler.Calls >= 2),
                "выполнено не меньше двух опросов getUpdates");

            await Task.Delay(200);

            received.Count.ShouldBe(1, "external_id дедуплицируется в окне памяти адаптера");
            received[0].ExternalId.ShouldBe($"{AdapterTestData.ChatId}:7");

            await adapter.DisposeAsync();
            await reader;
        }
        finally
        {
            await adapter.DisposeAsync();
        }
    }

    [Fact]
    public async Task Bot_api_auth_error_stops_polling()
    {
        var recorder = new HealthRecorder();
        var handler = new BotApiHandler(
            _ => """{"ok":false,"description":"Unauthorized"}""",
            _ => HttpStatusCode.Unauthorized);

        using var http = new HttpClient(handler);
        var adapter = new TelegramBotApiAdapter(
            AdapterTestData.Config(
                mode: TelegramSourceSettings.BotMode,
                session: AdapterTestData.BotToken,
                reportHealth: recorder.Callback),
            http,
            TimeProvider.System,
            apiBaseUrl: "http://bot.local");

        try
        {
            await adapter.StartAsync(CancellationToken.None);

            await WorkerTestContext.WaitUntilAsync(
                () => Task.FromResult(recorder.Has(SourceState.AuthError)),
                "401 переводит источник в auth_error");

            recorder.Last(SourceState.AuthError)!.Detail.ShouldNotBeNull()
                .ShouldContain("401");

            // Недействительный токен не опрашивается повторно: переподключения бессмысленны.
            await Task.Delay(300);
            handler.Calls.ShouldBe(1);
            (await adapter.CheckHealthAsync(CancellationToken.None)).State
                .ShouldBe(SourceState.AuthError);
        }
        finally
        {
            await adapter.DisposeAsync();
        }
    }

    [Fact]
    public async Task Bot_api_flood_wait_reports_and_resumes()
    {
        var time = new ManualTimeProvider(Now);
        var recorder = new HealthRecorder();
        var handler = new BotApiHandler(
            call => call == 1
                ? """{"ok":false,"parameters":{"retry_after":7}}"""
                : AdapterTestData.GetUpdates(AdapterTestData.TelegramMessage(5, "Вода есть")),
            call => call == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);

        using var http = new HttpClient(handler);
        var adapter = new TelegramBotApiAdapter(
            AdapterTestData.Config(
                mode: TelegramSourceSettings.BotMode,
                session: AdapterTestData.BotToken,
                reportHealth: recorder.Callback),
            http,
            time,
            apiBaseUrl: "http://bot.local");

        var received = new List<RawMessage>();
        try
        {
            await adapter.StartAsync(CancellationToken.None);
            var reader = CollectAsync(adapter, received);

            await WorkerTestContext.WaitUntilAsync(
                () => Task.FromResult(recorder.Has(SourceState.FloodWait)),
                "429 с retry_after переводит источник в flood_wait");

            recorder.Last(SourceState.FloodWait)!.Detail.ShouldBe(
                (Now + TimeSpan.FromSeconds(7)).ToString("O", CultureInfo.InvariantCulture));

            await WorkerTestContext.WaitUntilAsync(
                () =>
                {
                    time.Advance(TimeSpan.FromSeconds(1));

                    return Task.FromResult(received.Count >= 1);
                },
                "после FloodWait адаптер вернулся к опросу и прочитал сообщение");

            received[0].ExternalId.ShouldBe($"{AdapterTestData.ChatId}:5");

            await adapter.DisposeAsync();
            await reader;
        }
        finally
        {
            await adapter.DisposeAsync();
        }
    }

    [Fact]
    public async Task Mtproto_adapter_maps_updates_to_raw_messages()
    {
        var recorder = new HealthRecorder();
        var incoming = new MtProtoIncomingMessage(
            42,
            "Подача воды прекращена",
            "житель",
            "Вода",
            AdapterTestData.ChatUsername,
            Now);

        var factory = new ScriptedTransportFactory(
            _ => new StubTelegramTransport(
                read: ct => TelegramTransportScripts.YieldThenBlock([incoming], ct)));

        var adapter = new TelegramMtProtoAdapter(
            AdapterTestData.Config(
                mode: TelegramSourceSettings.UserbotMode,
                reportHealth: recorder.Callback),
            factory);

        var received = new List<RawMessage>();
        try
        {
            await adapter.StartAsync(CancellationToken.None);
            var reader = CollectAsync(adapter, received);

            await WorkerTestContext.WaitUntilAsync(
                () => Task.FromResult(received.Count >= 1),
                "сообщение MTProto прочитано");

            var message = received[0];
            message.SourceId.ShouldBe(adapter.SourceId);
            message.ExternalId.ShouldBe($"{AdapterTestData.ChatId}:42");
            message.Text.ShouldBe("Подача воды прекращена");
            message.Author.ShouldBe("житель");
            message.Chat.ShouldBe("Вода");
            message.Link.ShouldBe($"https://t.me/{AdapterTestData.ChatUsername}/42");
            message.PostedAt.ShouldBe(Now);

            await WorkerTestContext.WaitUntilAsync(
                () => Task.FromResult(recorder.Has(SourceState.Ok)),
                "health ok после получения сообщения");

            await adapter.DisposeAsync();
            await reader;
        }
        finally
        {
            await adapter.DisposeAsync();
        }
    }

    [Fact]
    public async Task Network_error_backoff_without_direct_connection()
    {
        var recorder = new HealthRecorder();
        var destination = new TcpListener(IPAddress.Loopback, 0);
        destination.Start();
        var destinationPort = ((IPEndPoint)destination.LocalEndpoint).Port;

        var proxy = new ProxySettings(
            "socks5",
            "127.0.0.1",
            AdapterTestData.UnusedPort());

        var adapter = new TelegramBotApiAdapter(
            AdapterTestData.Config(
                mode: TelegramSourceSettings.BotMode,
                session: AdapterTestData.BotToken,
                proxy: proxy,
                proxyRequired: true,
                reportHealth: recorder.Callback),
            httpClient: null,
            TimeProvider.System,
            apiBaseUrl: $"http://127.0.0.1:{destinationPort}");

        try
        {
            await adapter.StartAsync(CancellationToken.None);

            await WorkerTestContext.WaitUntilAsync(
                () => Task.FromResult(recorder.Has(SourceState.Error)),
                "недоступный прокси переводит источник в error");

            var accepted = destination.AcceptTcpClientAsync();
            await Task.Delay(200);

            accepted.IsCompleted.ShouldBeFalse(
                "прямое соединение с целевым хостом при недоступном прокси запрещено ");

            (await adapter.CheckHealthAsync(CancellationToken.None)).State
                .ShouldBe(SourceState.Error);
        }
        finally
        {
            await adapter.DisposeAsync();
            destination.Stop();
        }
    }

    [Fact]
    public void Adapter_factory_selection_by_mode()
    {
        var botFactory = new TelegramBotApiAdapterFactory(TimeProvider.System);
        var mtprotoFactory = new TelegramMtProtoAdapterFactory(
            TimeProvider.System,
            new ScriptedTransportFactory(_ => new StubTelegramTransport()));

        var botConfig = AdapterTestData.Config(
            mode: TelegramSourceSettings.BotMode,
            session: AdapterTestData.BotToken);
        var userbotConfig = AdapterTestData.Config(mode: TelegramSourceSettings.UserbotMode);
        var defaultConfig = AdapterTestData.Config();

        botFactory.CanHandle(botConfig).ShouldBeTrue();
        botFactory.CanHandle(userbotConfig).ShouldBeFalse();
        botFactory.CanHandle(defaultConfig).ShouldBeFalse();

        mtprotoFactory.CanHandle(userbotConfig).ShouldBeTrue();
        mtprotoFactory.CanHandle(defaultConfig).ShouldBeTrue();
        mtprotoFactory.CanHandle(botConfig).ShouldBeFalse();

        Should.Throw<NotSupportedException>(() => botFactory.Create(userbotConfig));
        Should.Throw<NotSupportedException>(() => mtprotoFactory.Create(botConfig));

        botFactory.Create(botConfig).ShouldBeOfType<TelegramBotApiAdapter>();
        mtprotoFactory.Create(defaultConfig).ShouldBeOfType<TelegramMtProtoAdapter>();
    }

    /// <summary>Читает поток адаптера в список до остановки адаптера.</summary>
    private static Task CollectAsync(IDataSource adapter, List<RawMessage> messages) =>
        Task.Run(
            async () =>
            {
                await foreach (var message in adapter.ReadAsync(CancellationToken.None))
                {
                    lock (messages)
                    {
                        messages.Add(message);
                    }
                }
            });

    private static async Task<List<RawMessage>> ReadMessagesAsync(
        IDataSource adapter,
        int count,
        CancellationToken ct)
    {
        var messages = new List<RawMessage>();

        await foreach (var message in adapter.ReadAsync(ct))
        {
            messages.Add(message);
            if (messages.Count >= count)
            {
                return messages;
            }
        }

        return messages;
    }
}
