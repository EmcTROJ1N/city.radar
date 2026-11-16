using System.Net;

using Api.IntegrationTests.Catalog;
using Api.IntegrationTests.Infrastructure;

using Npgsql;

using Shouldly;

namespace Api.IntegrationTests.Moderation;

/// <summary>
/// Жалобы пользователей:
/// <c>POST /objects/{id}/complaints</c> создаёт кейс <c>complaint</c> с автором, ограничения
/// тела и права, а также появление жалобы в очереди модерации.
/// </summary>
/// <remarks>
/// БД общая на коллекцию, поэтому перед каждым тестом очищается <c>moderation_cases</c>:
/// иначе проверка очереди видела бы кейсы других тестов. Rate limit пока не реализован
/// — соответствующий тест помечен <c>Skip</c>.
/// </remarks>
[Collection("postgres")]
public sealed class ComplaintsTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string ComplaintsPath = "/api/v1/objects";
    private const string CasesPath = "/api/v1/moderation/cases";
    private const string ObjectsPath = "/api/v1/catalog/objects";

    private CatalogApi _api = null!;

    public async Task InitializeAsync()
    {
        _api = new CatalogApi(fixture);
        await _api.InitializeAsync();

        await _api.ExecuteAsync("truncate table moderation_cases;");
        await _api.ExecuteAsync(
            "insert into global_settings (key, value) values ('comment_max_length', '500'::jsonb) "
            + "on conflict (key) do update set value = excluded.value");
    }

    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task User_complaint_creates_case_with_reporter()
    {
        var ct = CancellationToken.None;
        var token = await _api.UserTokenAsync(ct);
        var objectId = await CreateObjectAsync(ct);
        const string text = "Статус не соответствует действительности";

        using var response = await PostComplaintAsync(objectId, token, $$"""{"text":"{{text}}"}""", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        var caseId = json.GetProperty("caseId").GetGuid();

        var row = await ReadCaseAsync(caseId, ct);
        row.ShouldNotBeNull();
        row!.Kind.ShouldBe("complaint");
        row.State.ShouldBe("open");
        row.ReporterId.ShouldBe(CatalogApi.UserId);
        row.MapObjectId.ShouldBe(objectId);
        row.Comment.ShouldBe(text);
        row.Proposed.ShouldBeNull();
    }

    [Fact]
    public async Task Complaint_requires_authentication()
    {
        var ct = CancellationToken.None;
        var objectId = await CreateObjectAsync(ct);

        using var response = await PostComplaintAsync(
            objectId,
            accessToken: null,
            """{"text":"Жалоба"}""",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Inactive_object_404()
    {
        var ct = CancellationToken.None;
        var token = await _api.UserTokenAsync(ct);
        var objectId = await CreateObjectAsync(ct, isActive: false);

        using var response = await PostComplaintAsync(
            objectId,
            token,
            """{"text":"Жалоба"}""",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Unknown_object_404()
    {
        var ct = CancellationToken.None;
        var token = await _api.UserTokenAsync(ct);

        using var response = await PostComplaintAsync(
            Guid.NewGuid(),
            token,
            """{"text":"Жалоба"}""",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Comment_over_limit_422()
    {
        var ct = CancellationToken.None;
        var token = await _api.UserTokenAsync(ct);
        var objectId = await CreateObjectAsync(ct);
        var text = new string('а', 501);

        using var response = await PostComplaintAsync(
            objectId,
            token,
            $$"""{"text":"{{text}}"}""",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var json = await CatalogApi.ReadJsonAsync(response, ct);
        json.GetProperty("errors").EnumerateArray()
            .ShouldContain(error => error.GetProperty("field").GetString() == "text");

        (await CountCasesAsync(ct)).ShouldBe(0L);
    }

    [Fact]
    public async Task Complaint_appears_in_moderation_queue_by_kind()
    {
        var ct = CancellationToken.None;
        var userToken = await _api.UserTokenAsync(ct);
        var moderatorToken = await _api.ModeratorTokenAsync(ct);
        var objectId = await CreateObjectAsync(ct);

        Guid caseId;
        using (var response = await PostComplaintAsync(
            objectId,
            userToken,
            """{"text":"Нет воды, но на карте зелёный статус"}""",
            ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            caseId = (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("caseId").GetGuid();
        }

        using var queue = await _api.GetAsync(
            $"{CasesPath}?kind=complaint&state=open",
            moderatorToken,
            ct);

        queue.StatusCode.ShouldBe(HttpStatusCode.OK);

        var items = (await CatalogApi.ReadJsonAsync(queue, ct))
            .GetProperty("items")
            .EnumerateArray()
            .ToArray();

        items.ShouldContain(item => item.GetProperty("id").GetGuid() == caseId
            && item.GetProperty("kind").GetString() == "complaint");
        items.ShouldAllBe(item => item.GetProperty("kind").GetString() == "complaint");
    }

    [Fact(Skip = "включится после + подключение IRateLimiter")]
    public Task Complaint_rate_limit_429() => Task.CompletedTask;

    // ------------------------------------------------------------------
    // Данные и запросы
    // ------------------------------------------------------------------

    private async Task<Guid> CreateObjectAsync(CancellationToken ct, bool isActive = true)
    {
        var token = await _api.ModeratorTokenAsync(ct);
        var suffix = CatalogApi.NewSuffix();

        using var response = await _api.SendAsync(
            HttpMethod.Post,
            ObjectsPath,
            token,
            $$"""
              {"name":"Жалоба {{suffix}}","coverageKind":"point",
               "geometry":{"type":"Point","coordinates":[37.80,48.01]},
               "isActive":{{isActive.ToString().ToLowerInvariant()}}}
              """,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await CatalogApi.ReadJsonAsync(response, ct)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> PostComplaintAsync(
        Guid objectId,
        string? accessToken,
        string json,
        CancellationToken ct) =>
        _api.SendAsync(
            HttpMethod.Post,
            $"{ComplaintsPath}/{objectId}/complaints",
            accessToken,
            json,
            ct);

    private async Task<long> CountCasesAsync(CancellationToken ct) =>
        await _api.CountAsync("select count(*) from moderation_cases", ct);

    private async Task<CaseRow?> ReadCaseAsync(Guid caseId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            select kind, state, reporter_id, map_object_id, comment, proposed::text
            from moderation_cases
            where id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", caseId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new CaseRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private sealed record CaseRow(
        string Kind,
        string State,
        Guid? ReporterId,
        Guid? MapObjectId,
        string? Comment,
        string? Proposed);
}
