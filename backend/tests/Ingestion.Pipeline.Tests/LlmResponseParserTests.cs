using System.Globalization;

using Shouldly;

namespace Ingestion.Pipeline.Tests;

/// <summary>
/// Тесты разбора ответа LLM: принимается чистый JSON и JSON в markdown-блоке,
/// мусор вокруг игнорируется, несоответствие схеме даёт <c>null</c> без исключений.
/// </summary>
public sealed class LlmResponseParserTests
{
    private const string ValidJson =
        """{"relevant": true, "status": "no_water", "object_id": null, "confidence": 0.92, "evidence_quote": "воду отключили"}""";

    [Fact]
    public void Parses_plain_json()
    {
        var parsed = ParseValid(ValidJson);

        parsed.Relevant.ShouldBeTrue();
        parsed.Status.ShouldBe("no_water");
        parsed.ObjectId.ShouldBeNull();
        parsed.Confidence.ShouldBe(0.92);
        parsed.EvidenceQuote.ShouldBe("воду отключили");
    }

    [Fact]
    public void Parses_json_in_markdown_block()
    {
        var parsed = ParseValid($"```json\n{ValidJson}\n```");

        parsed.Status.ShouldBe("no_water");
    }

    [Fact]
    public void Parses_json_in_markdown_block_without_language()
    {
        var parsed = ParseValid("```\n{\"relevant\": false}\n```");

        parsed.Relevant.ShouldBeFalse();
    }

    [Theory]
    [InlineData("Вот ответ: {\"relevant\": false} — надеюсь, помог.")]
    [InlineData("\n\n  {\"relevant\": false}  \n\n")]
    [InlineData("```json\n{\"relevant\": false}\n```\nКомментарий модели после блока.")]
    public void Ignores_text_around_json(string raw)
    {
        ParseValid(raw).Relevant.ShouldBeFalse();
    }

    [Fact]
    public void Invalid_json_before_valid_object_is_skipped()
    {
        ParseValid("prefix { not json } then {\"relevant\": false} tail").Relevant.ShouldBeFalse();
    }

    [Fact]
    public void First_valid_object_wins()
    {
        const string raw =
            """{"relevant": false} {"relevant": true, "status": "no_water", "confidence": 0.9, "evidence_quote": "вода"}""";

        ParseValid(raw).Relevant.ShouldBeFalse();
    }

    [Fact]
    public void Handles_braces_and_escaped_quotes_inside_strings()
    {
        const string raw =
            """{"relevant": true, "status": "no_water", "confidence": 0.9, "evidence_quote": "скобка } и { и \"кавычки\" тут", "extra": {"nested": [1, 2, {"x": "y"}]}}""";

        ParseValid(raw).EvidenceQuote.ShouldBe("скобка } и { и \"кавычки\" тут");
    }

    [Fact]
    public void Object_id_is_optional()
    {
        const string withoutObjectId =
            """{"relevant": true, "status": "no_water", "confidence": 0.9, "evidence_quote": "вода"}""";
        const string withNullObjectId =
            """{"relevant": true, "status": "no_water", "object_id": null, "confidence": 0.9, "evidence_quote": "вода"}""";

        ParseValid(withoutObjectId).ObjectId.ShouldBeNull();
        ParseValid(withNullObjectId).ObjectId.ShouldBeNull();
    }

    [Fact]
    public void Object_id_is_preserved_when_present()
    {
        var id = Guid.NewGuid();
        var raw =
            $"{{\"relevant\": true, \"status\": \"no_water\", \"object_id\": \"{id:D}\", \"confidence\": 0.9, \"evidence_quote\": \"вода\"}}";

        ParseValid(raw).ObjectId.ShouldBe(id.ToString("D"));
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        const string raw =
            """{"relevant": false, "reason": "спам", "tokens": 12, "nested": {"a": 1}}""";

        ParseValid(raw).Relevant.ShouldBeFalse();
    }

    [Fact]
    public void Relevant_false_without_other_fields_is_valid()
    {
        var parsed = ParseValid("""{"relevant": false}""");

        parsed.Relevant.ShouldBeFalse();
        parsed.Status.ShouldBeNull();
        parsed.ObjectId.ShouldBeNull();
        parsed.Confidence.ShouldBe(0d);
        parsed.EvidenceQuote.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("не JSON")]
    [InlineData("{")]
    [InlineData("{\"relevant\": }")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("```json\n```")]
    public void Invalid_or_empty_input_returns_null(string raw)
    {
        LlmResponseParser.Parse(raw).ShouldBeNull();
    }

    [Fact]
    public void Null_input_returns_null()
    {
        LlmResponseParser.Parse(null).ShouldBeNull();
    }

    [Theory]
    [InlineData("""{"status": "no_water", "confidence": 0.9, "evidence_quote": "вода"}""")]
    [InlineData("""{"relevant": true, "confidence": 0.9, "evidence_quote": "вода"}""")]
    [InlineData("""{"relevant": true, "status": "no_water", "evidence_quote": "вода"}""")]
    [InlineData("""{"relevant": true, "status": "no_water", "confidence": 0.9}""")]
    [InlineData("""{"relevant": true, "status": "no_water", "confidence": 0.9, "evidence_quote": null}""")]
    [InlineData("""{"relevant": true, "status": null, "confidence": 0.9, "evidence_quote": "вода"}""")]
    [InlineData("""{"relevant": true, "status": "", "confidence": 0.9, "evidence_quote": "вода"}""")]
    [InlineData("""{"relevant": true}""")]
    public void Missing_required_field_returns_null(string raw)
    {
        LlmResponseParser.Parse(raw).ShouldBeNull();
    }

    [Theory]
    [InlineData("""{"relevant": "true", "status": "no_water", "confidence": 0.9, "evidence_quote": "вода"}""")]
    [InlineData("""{"relevant": 1, "status": "no_water", "confidence": 0.9, "evidence_quote": "вода"}""")]
    [InlineData("""{"relevant": null, "status": "no_water", "confidence": 0.9, "evidence_quote": "вода"}""")]
    [InlineData("""{"relevant": true, "status": 1, "confidence": 0.9, "evidence_quote": "вода"}""")]
    [InlineData("""{"relevant": true, "status": ["no_water"], "confidence": 0.9, "evidence_quote": "вода"}""")]
    [InlineData("""{"relevant": true, "status": "no_water", "confidence": "0.9", "evidence_quote": "вода"}""")]
    [InlineData("""{"relevant": true, "status": "no_water", "confidence": true, "evidence_quote": "вода"}""")]
    [InlineData("""{"relevant": true, "status": "no_water", "confidence": null, "evidence_quote": "вода"}""")]
    [InlineData("""{"relevant": true, "status": "no_water", "confidence": 0.9, "evidence_quote": 1}""")]
    [InlineData("""{"relevant": true, "status": "no_water", "confidence": 0.9, "evidence_quote": {"text": "вода"}}""")]
    [InlineData("""{"relevant": true, "status": "no_water", "object_id": 42, "confidence": 0.9, "evidence_quote": "вода"}""")]
    [InlineData("""{"relevant": true, "status": "no_water", "object_id": {"id": "x"}, "confidence": 0.9, "evidence_quote": "вода"}""")]
    public void Wrong_field_type_returns_null(string raw)
    {
        LlmResponseParser.Parse(raw).ShouldBeNull();
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(-1)]
    [InlineData(1.0001)]
    [InlineData(2)]
    public void Confidence_outside_range_returns_null(double confidence)
    {
        LlmResponseParser.Parse(JsonWithConfidence(confidence)).ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(1)]
    public void Confidence_at_range_boundaries_is_valid(double confidence)
    {
        ParseValid(JsonWithConfidence(confidence)).Confidence.ShouldBe(confidence);
    }

    [Fact]
    public void Evidence_quote_is_not_trimmed()
    {
        const string raw =
            """{"relevant": true, "status": "no_water", "confidence": 0.5, "evidence_quote": " воду отключили "}""";

        ParseValid(raw).EvidenceQuote.ShouldBe(" воду отключили ");
    }

    [Fact]
    public void Status_is_trimmed()
    {
        const string raw =
            """{"relevant": true, "status": " no_water ", "confidence": 0.5, "evidence_quote": "вода"}""";

        ParseValid(raw).Status.ShouldBe("no_water");
    }

    [Fact]
    public void Empty_object_id_becomes_null()
    {
        const string raw =
            """{"relevant": true, "status": "no_water", "object_id": "  ", "confidence": 0.5, "evidence_quote": "вода"}""";

        ParseValid(raw).ObjectId.ShouldBeNull();
    }

    [Fact]
    public void Huge_invalid_input_does_not_throw()
    {
        var huge = new string('x', 1_000_000) + "{";

        LlmResponseParser.Parse(huge).ShouldBeNull();
    }

    [Fact]
    public void Huge_valid_json_is_parsed()
    {
        var quote = new string('ж', 500_000);
        var raw = "{\"relevant\": true, \"status\": \"no_water\", \"confidence\": 0.5, \"evidence_quote\": \""
            + quote + "\"}";

        ParseValid(raw).EvidenceQuote.Length.ShouldBe(quote.Length);
    }

    private static ParsedLlmResponse ParseValid(string raw)
    {
        var parsed = LlmResponseParser.Parse(raw);
        parsed.ShouldNotBeNull();
        return parsed!;
    }

    private static string JsonWithConfidence(double confidence) =>
        $"{{\"relevant\": true, \"status\": \"no_water\", \"confidence\": {confidence.ToString(CultureInfo.InvariantCulture)}, \"evidence_quote\": \"вода\"}}";
}
