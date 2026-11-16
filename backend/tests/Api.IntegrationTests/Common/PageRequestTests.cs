using BuildingBlocks.Web;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Api.IntegrationTests.Common;

/// <summary>
/// Параметры курсорной пагинации из контракта: <c>limit</c> от 1 до 100 (по умолчанию 20)
/// и непрозрачный <c>cursor</c>. Тесты чистые, БД не нужна.
/// </summary>
public sealed class PageRequestTests
{
    [Fact]
    public void Default_limit_is_20()
    {
        PageRequest.TryCreate(null, null, out var paging, out var failure).ShouldBeTrue();

        failure.ShouldBeNull();
        paging.Limit.ShouldBe(PageRequest.DefaultLimit);
        paging.HasCursor.ShouldBeFalse();
        paging.CursorPayload.ShouldBeNull();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(100)]
    public void Limit_inside_bounds_is_accepted(int limit)
    {
        PageRequest.TryCreate(limit, null, out var paging, out var failure).ShouldBeTrue();

        failure.ShouldBeNull();
        paging.Limit.ShouldBe(limit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task Limit_outside_bounds_is_bad_request(int limit)
    {
        PageRequest.TryCreate(limit, null, out _, out var failure).ShouldBeFalse();

        await AssertBadRequestAsync(failure);
    }

    [Fact]
    public void Cursor_payload_is_decoded()
    {
        var cursor = PageRequest.EncodeCursor("42");

        PageRequest.TryCreate(null, cursor, out var paging, out var failure).ShouldBeTrue();

        failure.ShouldBeNull();
        paging.HasCursor.ShouldBeTrue();
        paging.CursorPayload.ShouldBe("42");
    }

    [Theory]
    [InlineData("не-курсор")]
    [InlineData("%%%")]
    [InlineData("a")]
    public async Task Broken_cursor_is_bad_request(string cursor)
    {
        PageRequest.TryCreate(null, cursor, out _, out var failure).ShouldBeFalse();

        await AssertBadRequestAsync(failure);
    }

    private static async Task AssertBadRequestAsync(IResult? result)
    {
        ArgumentNullException.ThrowIfNull(result);

        await using var services = new ServiceCollection()
            .AddLogging()
            .AddProblemDetails()
            .BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();

        await result.ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        context.Response.ContentType.ShouldStartWith("application/problem+json");
    }
}
