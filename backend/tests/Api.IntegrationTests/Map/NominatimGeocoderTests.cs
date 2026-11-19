using System.Net;
using System.Text;

using BuildingBlocks.Web.Caching;

using MapReadModel.Core.Geocoding;

using Shouldly;

namespace Api.IntegrationTests.Map;

/// <summary>
/// Геокодер Nominatim: формат запроса, нормализация адреса, кэш найденных и не найденных
/// адресов (Redis и память процесса), ошибки сети не кэшируются. Без БД и сети — ответы подменены.
/// </summary>
public sealed class NominatimGeocoderTests
{
    private static readonly GeoBox Zone = new(37.770, 48.033, 37.787, 48.042);

    private static readonly GeocodingOptions Options = new()
    {
        NominatimUrl = new Uri("https://nominatim.test"),
        UserAgent = "mayak-test/1.0",
        City = "Донецк",
        Enabled = true,
        FailurePause = TimeSpan.Zero,
    };

    [Fact]
    public void Normalize_unifies_spelling()
    {
        AddressNormalizer.Normalize("ул. Тренёва, 3в").ShouldBe(AddressNormalizer.Normalize("Тренева  3в"));
        AddressNormalizer.Normalize("Киевский проспект, 10").ShouldBe(AddressNormalizer.Normalize("киевский 10"));
        AddressNormalizer.Normalize("Тренёва 3в").ShouldNotBe(AddressNormalizer.Normalize("Тренёва 3б"));
    }

    [Fact]
    public async Task Geocode_sends_expected_query()
    {
        var handler = new StubHandler(_ => Found());
        using var geocoder = new NominatimGeocoder(Options, new DictionaryCache(), TimeProvider.System, handler);

        var point = await geocoder.GeocodeAsync("Тренёва 3в", Zone, null, CancellationToken.None);

        point.ShouldBe(new GeoPoint(48.0351, 37.7801));
        var request = handler.Requests.ShouldHaveSingleItem();
        request.UserAgent.ShouldBe("mayak-test/1.0");
        var query = Uri.UnescapeDataString(request.Uri.Query);
        query.ShouldContain("q=Тренёва 3в, Донецк");
        query.ShouldContain("format=jsonv2");
        query.ShouldContain("limit=1");
        query.ShouldContain("accept-language=ru");
        query.ShouldContain("bounded=1");
        query.ShouldContain("viewbox=");
    }

    [Fact]
    public async Task Geocode_caches_hit_and_miss()
    {
        var handler = new StubHandler(request =>
            Uri.UnescapeDataString(request.RequestUri!.Query).Contains("3в", StringComparison.Ordinal) ? Found() : Json("[]"));
        using var geocoder = new NominatimGeocoder(Options, new DictionaryCache(), TimeProvider.System, handler);

        (await geocoder.GeocodeAsync("Тренёва 3в", Zone, null, CancellationToken.None)).ShouldNotBeNull();
        (await geocoder.GeocodeAsync("ул. Тренева, 3в", Zone, null, CancellationToken.None)).ShouldNotBeNull();
        (await geocoder.GeocodeAsync("Нет такой 999", Zone, null, CancellationToken.None)).ShouldBeNull();
        (await geocoder.GeocodeAsync("Нет такой 999", Zone, null, CancellationToken.None)).ShouldBeNull();

        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Geocode_does_not_cache_errors()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var geocoder = new NominatimGeocoder(Options, new DictionaryCache(), TimeProvider.System, handler);

        (await geocoder.GeocodeAsync("Тренёва 3в", Zone, null, CancellationToken.None)).ShouldBeNull();
        (await geocoder.GeocodeAsync("Тренёва 3в", Zone, null, CancellationToken.None)).ShouldBeNull();

        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Geocode_pauses_network_after_failure()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var geocoder = new NominatimGeocoder(
            Options with { FailurePause = TimeSpan.FromMinutes(1) },
            new DictionaryCache(),
            TimeProvider.System,
            handler);

        (await geocoder.GeocodeAsync("Тренёва 3в", Zone, null, CancellationToken.None)).ShouldBeNull();
        (await geocoder.GeocodeAsync("Тренёва 5в", Zone, null, CancellationToken.None)).ShouldBeNull();

        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Geocode_uses_memory_cache_without_redis()
    {
        var handler = new StubHandler(_ => Found());
        using var geocoder = new NominatimGeocoder(Options, new NullResponseCache(), TimeProvider.System, handler);

        await geocoder.GeocodeAsync("Тренёва 3в", Zone, null, CancellationToken.None);
        await geocoder.GeocodeAsync("Тренёва 3в", Zone, null, CancellationToken.None);

        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Geocode_respects_budget_but_cache_hits_are_free()
    {
        var handler = new StubHandler(_ => Found());
        using var geocoder = new NominatimGeocoder(Options, new DictionaryCache(), TimeProvider.System, handler);
        await geocoder.GeocodeAsync("Тренёва 3в", Zone, null, CancellationToken.None);

        var budget = new GeocodeBudget(1);
        (await geocoder.GeocodeAsync("Тренёва 3в", Zone, budget, CancellationToken.None)).ShouldNotBeNull();
        (await geocoder.GeocodeAsync("Тренёва 5в", Zone, budget, CancellationToken.None)).ShouldNotBeNull();
        (await geocoder.GeocodeAsync("Тренёва 7", Zone, budget, CancellationToken.None)).ShouldBeNull();

        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Disabled_geocoder_does_not_call_nominatim()
    {
        var handler = new StubHandler(_ => Found());
        using var geocoder = new NominatimGeocoder(
            Options with { Enabled = false },
            new DictionaryCache(),
            TimeProvider.System,
            handler);

        (await geocoder.GeocodeAsync("Тренёва 3в", Zone, null, CancellationToken.None)).ShouldBeNull();
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public void Options_have_documented_defaults()
    {
        var options = GeocodingOptions.FromConfiguration(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        options.NominatimUrl.ShouldBe(new Uri("https://nominatim.openstreetmap.org"));
        options.UserAgent.ShouldBe("mayak-water/1.0");
        options.City.ShouldBe("Донецк");
        options.Enabled.ShouldBeTrue();
    }

    private static HttpResponseMessage Found() =>
        Json("""[{"lat":"48.0351","lon":"37.7801","display_name":"3в, Треньова вулиця"}]""");

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record SeenRequest(Uri Uri, string UserAgent);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<SeenRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new SeenRequest(request.RequestUri!, request.Headers.UserAgent.ToString()));
            return Task.FromResult(respond(request));
        }
    }

    private sealed class DictionaryCache : IResponseCache
    {
        private readonly Dictionary<string, string> _values = [];

        public Task<string?> GetAsync(string key, CancellationToken ct) =>
            Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }
}
