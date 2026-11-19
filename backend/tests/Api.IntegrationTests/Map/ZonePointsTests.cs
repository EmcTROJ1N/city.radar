using System.Net;
using System.Text.Json;

using Api.IntegrationTests.Infrastructure;

using MapReadModel.Core.Geocoding;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using Shouldly;

namespace Api.IntegrationTests.Map;

/// <summary>
/// <c>GET /map/objects/{id}/points</c>: временные точки по адресам актуальных сообщений зоны.
/// Последнее сообщение по адресу побеждает, точки вне зоны отбрасываются, новых геокодирований
/// за вызов не больше пяти, доступ анонимный. Геокодер подменён: координаты задаёт тест.
/// </summary>
[Collection("postgres")]
public sealed class ZonePointsTests(PostgresFixture fixture) : IAsyncLifetime
{
    // Зона 37.70–37.71 × 48.10–48.11; точки внутри — по адресу, снаружи — «Далёкая».
    private const string ZonePolygon = "POLYGON((37.70 48.10, 37.71 48.10, 37.71 48.11, 37.70 48.11, 37.70 48.10))";

    private readonly StubGeocoder _geocoder = new();
    private TestApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new TestApiFactory(
            fixture.ConnectionString,
            services => services.Replace(ServiceDescriptor.Singleton<IGeocoder>(_geocoder)));
        _client = _factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Latest_message_per_address_wins()
    {
        var zone = await CreateZoneAsync();
        await AddMessageAsync(zone, "ул. Тренёва, 3в", "none", minutesAgo: 40, text: "Тренёва 3в, воды нет");
        await AddMessageAsync(zone, "Тренева 3в", "ok", minutesAgo: 10, text: "Тренева 3в пошла");

        var item = (await GetItemsAsync(zone.ObjectId)).ShouldHaveSingleItem();

        item.GetProperty("address").GetString().ShouldBe("Тренева 3в");
        item.GetProperty("status").GetProperty("code").GetString().ShouldBe("ok");
        item.GetProperty("text").GetString().ShouldBe("Тренева 3в пошла");
        item.GetProperty("observationCount").GetInt32().ShouldBe(2);
        item.GetProperty("location").GetProperty("lat").GetDouble().ShouldBe(48.105, 0.0001);
    }

    [Fact]
    public async Task Point_outside_zone_is_dropped()
    {
        var zone = await CreateZoneAsync();
        await AddMessageAsync(zone, "Далёкая 1", "none", minutesAgo: 5);
        await AddMessageAsync(zone, "Тренёва 3в", "ok", minutesAgo: 5);

        var items = await GetItemsAsync(zone.ObjectId);

        items.ShouldHaveSingleItem().GetProperty("address").GetString().ShouldBe("Тренёва 3в");
    }

    [Fact]
    public async Task At_most_five_new_geocodes_per_call()
    {
        var zone = await CreateZoneAsync();
        for (var house = 1; house <= 7; house++)
        {
            await AddMessageAsync(zone, $"Тренёва {house}", "ok", minutesAgo: house);
        }

        (await GetItemsAsync(zone.ObjectId)).Count.ShouldBe(5);
        _geocoder.NetworkCalls.ShouldBe(5);
    }

    [Fact]
    public async Task Slow_geocoder_does_not_block_cached_points()
    {
        var zone = await CreateZoneAsync();
        _geocoder.Cached.Add("Тренёва 3в");
        await AddMessageAsync(zone, "Тренёва 3в", "ok", minutesAgo: 30);
        await AddMessageAsync(zone, "Медленная 1", "none", minutesAgo: 5);

        var started = DateTimeOffset.UtcNow;
        var items = await GetItemsAsync(zone.ObjectId);

        (DateTimeOffset.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(10));
        items.ShouldHaveSingleItem().GetProperty("address").GetString().ShouldBe("Тренёва 3в");
    }

    [Fact]
    public async Task Expired_retracted_and_addressless_messages_are_ignored()
    {
        var zone = await CreateZoneAsync();
        await AddMessageAsync(zone, "Тренёва 1", "ok", minutesAgo: 60 * 7);
        await AddMessageAsync(zone, "Тренёва 2", "ok", minutesAgo: 5, retracted: true);
        await AddMessageAsync(zone, null, "ok", minutesAgo: 5);

        (await GetItemsAsync(zone.ObjectId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Unknown_object_is_404()
    {
        using var response = await _client.GetAsync($"/api/v1/map/objects/{Guid.NewGuid()}/points");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Point_object_returns_empty()
    {
        var zone = await CreateZoneAsync(geometry: "POINT(37.705 48.105)", coverageKind: "point");
        await AddMessageAsync(zone, "Тренёва 3в", "ok", minutesAgo: 5);

        (await GetItemsAsync(zone.ObjectId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Too_many_requests_from_one_ip_get_429()
    {
        var zone = await CreateZoneAsync();
        await using var factory = new TestApiFactory(
            fixture.ConnectionString,
            services => services.Replace(ServiceDescriptor.Singleton<IGeocoder>(_geocoder)),
            new Dictionary<string, string?>
            {
                ["RateLimiting:Enabled"] = "true",
                ["RateLimiting:PointsIpLimit"] = "1",
                ["RateLimiting:PointsIpWindowSeconds"] = "60",
            });
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        using (var first = await client.GetAsync($"/api/v1/map/objects/{zone.ObjectId}/points"))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var second = await client.GetAsync($"/api/v1/map/objects/{zone.ObjectId}/points");
        second.StatusCode.ShouldBe((HttpStatusCode)429);
        second.Headers.RetryAfter.ShouldNotBeNull();
    }

    private async Task<List<JsonElement>> GetItemsAsync(Guid objectId)
    {
        // Без токена: точки видны анонимно.
        using var response = await _client.GetAsync($"/api/v1/map/objects/{objectId}/points");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToList();
    }

    private async Task<Zone> CreateZoneAsync(string geometry = ZonePolygon, string coverageKind = "polygon")
    {
        var zone = new Zone(Guid.NewGuid(), Guid.NewGuid());

        await ExecuteAsync(
            $"""
             insert into sources (id, name, source_type, settings)
             values ('{zone.SourceId}', 'Точки {zone.SourceId:N}', 'telegram_chat', '{"{"}{"}"}');
             insert into source_policies (source_id, timeout) values ('{zone.SourceId}', interval '6 hours');
             insert into source_statuses (source_id, code, name, color, icon) values
               ('{zone.SourceId}', 'ok', 'Вода есть', 'ok', 'drop'),
               ('{zone.SourceId}', 'none', 'Воды нет', 'none', 'cross');
             insert into map_objects (id, object_type_id, name, coverage_kind, geometry)
             values ('{zone.ObjectId}', '00000000-0000-0000-0000-0000000000b1', 'Зона {zone.ObjectId:N}',
                     '{coverageKind}', '{geometry}'::geography);
             """);

        return zone;
    }

    private Task AddMessageAsync(
        Zone zone,
        string? address,
        string statusCode,
        int minutesAgo,
        string? text = null,
        bool retracted = false)
    {
        var rawMessageId = Guid.NewGuid();
        var addressSql = address is null ? "null" : $"'{address}'";

        return ExecuteAsync(
            $"""
             insert into raw_messages (id, source_id, external_id, text, posted_at)
             values ('{rawMessageId}', '{zone.SourceId}', 'm-{rawMessageId:N}', '{text ?? address ?? "без адреса"}',
                     now() - interval '{minutesAgo} minutes');
             insert into observations
               (map_object_id, source_id, source_status_id, source_type, observed_at, expires_at, confidence,
                raw_message_id, address, is_retracted, retracted_at)
             select '{zone.ObjectId}', '{zone.SourceId}', ss.id, 'telegram',
                    now() - interval '{minutesAgo} minutes',
                    now() - interval '{minutesAgo} minutes' + interval '6 hours',
                    0.9, '{rawMessageId}', {addressSql},
                    {(retracted ? "true" : "false")}, {(retracted ? "now()" : "null")}
             from source_statuses ss where ss.source_id = '{zone.SourceId}' and ss.code = '{statusCode}';
             """);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record Zone(Guid SourceId, Guid ObjectId);

    /// <summary>
    /// «Тренёва …» — внутри зоны, «Далёкая …» — в 1 км к востоку, «Медленная …» — зависает до отмены.
    /// Вызов вне <see cref="Cached"/> считается новым запросом к сети и расходует бюджет.
    /// </summary>
    private sealed class StubGeocoder : IGeocoder
    {
        public int NetworkCalls { get; private set; }

        /// <summary>Адреса «из кэша»: отвечают сразу и бюджет не тратят.</summary>
        public HashSet<string> Cached { get; } = [];

        public async Task<GeoPoint?> GeocodeAsync(string address, GeoBox hint, GeocodeBudget? budget, CancellationToken ct)
        {
            if (Cached.Contains(address))
            {
                return new GeoPoint(48.105, 37.705);
            }

            if (budget is not null && !budget.TryTake())
            {
                return null;
            }

            NetworkCalls++;
            if (address.StartsWith("Медленная", StringComparison.Ordinal))
            {
                // Геокодер «завис»: отвечает только отменой.
                await Task.Delay(Timeout.Infinite, ct);
            }

            return address.StartsWith("Далёкая", StringComparison.Ordinal)
                ? new GeoPoint(48.105, 37.725)
                : new GeoPoint(48.105, 37.705);
        }
    }
}
