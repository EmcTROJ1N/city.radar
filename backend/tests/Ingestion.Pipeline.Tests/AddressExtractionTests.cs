using Catalog.Contracts;

using Ingestion.Abstractions;

using Shouldly;

using Sources.Contracts;

namespace Ingestion.Pipeline.Tests;

/// <summary>
/// Адрес из сообщения: LLM возвращает необязательное поле <c>address</c>, код его
/// обрезает и отбрасывает недопустимые значения, не отклоняя статус, и передаёт дальше
/// в предложения наблюдений и карточки модерации.
/// </summary>
public sealed class AddressExtractionTests
{
    private static readonly Guid SourceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RawMessageId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset PostedAt = new(2026, 11, 18, 10, 30, 0, TimeSpan.Zero);

    private const string MessageText = "Тренёва 3в, 5 этаж, воду отключили";

    private static readonly SourceStatusInfo NoWater =
        new(Guid.NewGuid(), "no_water", "Нет воды", "Подача воды прекращена.", "none", "cross");

    [Fact]
    public void Parse_reads_optional_address()
    {
        var parsed = LlmResponseParser.Parse(
            """{"relevant": true, "status": "no_water", "confidence": 0.9, "evidence_quote": "x", "address": " Тренёва 3в "}""");

        parsed.ShouldNotBeNull().Address.ShouldBe("Тренёва 3в");
    }

    [Theory]
    [InlineData("""{"relevant": true, "status": "no_water", "confidence": 0.9, "evidence_quote": "x", "address": 42}""")]
    [InlineData("""{"relevant": true, "status": "no_water", "confidence": 0.9, "evidence_quote": "x", "address": "  "}""")]
    [InlineData("""{"relevant": true, "status": "no_water", "confidence": 0.9, "evidence_quote": "x"}""")]
    public void Parse_ignores_missing_or_invalid_address(string raw)
    {
        var parsed = LlmResponseParser.Parse(raw);

        parsed.ShouldNotBeNull().Status.ShouldBe("no_water");
        parsed.Address.ShouldBeNull();
    }

    [Theory]
    [InlineData("Київський проспект 10")]
    [InlineData("Ґрунтова 1")]
    public void SanitizeAddress_drops_ukrainian_letters(string address)
    {
        ExtractionValidator.SanitizeAddress(address).ShouldBeNull();
    }

    [Fact]
    public void SanitizeAddress_drops_too_long_address()
    {
        ExtractionValidator.SanitizeAddress(new string('а', 121)).ShouldBeNull();
        ExtractionValidator.SanitizeAddress(new string('а', 120)).ShouldBe(new string('а', 120));
    }

    [Fact]
    public void SanitizeAddress_keeps_russian_address()
    {
        ExtractionValidator.SanitizeAddress("Киевский проспект 10").ShouldBe("Киевский проспект 10");
    }

    [Fact]
    public void Prompt_describes_address_field()
    {
        var (system, _) = SystemPromptBuilder.Build(new SourcePromptContext([NoWater], "вода", null));

        system.ShouldContain("\"address\"");
    }

    [Fact]
    public async Task Pipeline_passes_address_to_proposal()
    {
        var decision = await ProcessAsync(Response(address: "Тренёва 3в"));

        decision.ShouldBeOfType<PipelineDecision.Propose>().Observations
            .ShouldHaveSingleItem().Address.ShouldBe("Тренёва 3в");
    }

    [Fact]
    public async Task Pipeline_drops_invalid_address_but_keeps_status()
    {
        var decision = await ProcessAsync(Response(address: "Київський проспект 10"));

        var proposal = decision.ShouldBeOfType<PipelineDecision.Propose>().Observations.ShouldHaveSingleItem();
        proposal.StatusCode.ShouldBe("no_water");
        proposal.Address.ShouldBeNull();
    }

    [Fact]
    public async Task Pipeline_passes_address_to_moderation()
    {
        var decision = await ProcessAsync(Response(address: "Тренёва 3в", confidence: 0.3));

        decision.ShouldBeOfType<PipelineDecision.Moderate>().Request.Address.ShouldBe("Тренёва 3в");
    }

    private static string Response(string? address, double confidence = 0.9) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            relevant = true,
            status = "no_water",
            confidence,
            evidence_quote = "воду отключили",
            address,
        });

    private static async Task<PipelineDecision> ProcessAsync(string llmResponse)
    {
        var snapshot = new SourceRuntimeSnapshot(
            SourceId,
            [NoWater],
            "вода",
            false,
            [new LinkedObject(Guid.NewGuid(), "Зона", null)],
            0.7,
            null);

        return await new MessagePipeline(new FixedLlmProvider(llmResponse)).ProcessAsync(
            new RawMessage(SourceId, "external-1", MessageText, null, "chat", null, PostedAt),
            RawMessageId,
            snapshot,
            CancellationToken.None);
    }

    private sealed class FixedLlmProvider(string response) : ILlmProvider
    {
        public Task<string> CompleteAsync(LlmRequest request, CancellationToken ct) => Task.FromResult(response);
    }
}
