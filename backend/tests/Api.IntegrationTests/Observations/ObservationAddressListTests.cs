using System.Net;

using Api.IntegrationTests.Catalog;
using Api.IntegrationTests.Infrastructure;

using Shouldly;

namespace Api.IntegrationTests.Observations;

/// <summary>Публичный список отметок отдаёт адрес из сообщения чата.</summary>
[Collection("postgres")]
public sealed class ObservationAddressListTests(PostgresFixture fixture) : IAsyncLifetime
{
    private CatalogApi _api = null!;

    public async Task InitializeAsync()
    {
        _api = new CatalogApi(fixture);
        await _api.InitializeAsync();
    }

    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task Public_list_returns_address()
    {
        var ct = CancellationToken.None;
        var sourceId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var rawMessageId = Guid.NewGuid();

        await _api.ExecuteAsync(
            $"""
             insert into sources (id, name, source_type, settings)
             values ('{sourceId}', 'Список {sourceId:N}', 'telegram_chat', '{"{"}{"}"}');
             insert into source_policies (source_id, timeout) values ('{sourceId}', interval '6 hours');
             insert into source_statuses (source_id, code, name, color, icon)
             values ('{sourceId}', 'none', 'Воды нет', 'none', 'cross');
             insert into map_objects (id, object_type_id, name, coverage_kind, geometry)
             values ('{objectId}', '00000000-0000-0000-0000-0000000000b1', 'Зона {objectId:N}', 'polygon',
                     'POLYGON((37.70 48.10, 37.71 48.10, 37.71 48.11, 37.70 48.11, 37.70 48.10))'::geography);
             insert into raw_messages (id, source_id, external_id, text, posted_at)
             values ('{rawMessageId}', '{sourceId}', 'm-{rawMessageId:N}', 'Тренёва 3в, воды нет', now() - interval '5 minutes');
             insert into observations
               (map_object_id, source_id, source_status_id, source_type, observed_at, expires_at, confidence,
                raw_message_id, address)
             select '{objectId}', '{sourceId}', ss.id, 'telegram', now() - interval '5 minutes',
                    now() + interval '5 hours', 0.9, '{rawMessageId}', 'Тренёва 3в'
             from source_statuses ss where ss.source_id = '{sourceId}';
             """,
            ct);

        using var response = await _api.GetAsync($"/api/v1/objects/{objectId}/observations", null, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var item = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        item.GetProperty("address").GetString().ShouldBe("Тренёва 3в");
    }
}
