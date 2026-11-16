using Shouldly;

namespace Ingestion.Pipeline.Tests;

/// <summary>
/// Тесты проверки ответа LLM кодом: статус — только
/// из списка источника, цитата — точная подстрока полного текста без нормализации регистра
/// и пробелов, фиксация — только из привязок источника и только в режиме «по тексту».
/// </summary>
public sealed class ValidatorTests
{
    private const string Message = "Сегодня утром воду отключили, обещают к вечеру.";

    private static readonly HashSet<string> StatusCodes = new(StringComparer.Ordinal)
    {
        "no_water",
        "water_ok",
    };

    private static readonly Guid FirstObject = Guid.Parse("0b3d1f3e-2b4a-4c56-8d90-000000000001");
    private static readonly Guid SecondObject = Guid.Parse("0b3d1f3e-2b4a-4c56-8d90-000000000002");

    private static readonly HashSet<Guid> SourceObjectIds = [FirstObject, SecondObject];

    [Fact]
    public void Valid_response_passes()
    {
        var result = Validate(Response());

        result.ShouldBe(ValidationResult.Valid);
    }

    [Fact]
    public void Status_not_in_list_is_invalid()
    {
        var result = Validate(Response(status: "power_out"));

        result.ShouldBe(ValidationResult.InvalidStatus);
    }

    [Fact]
    public void Status_comparison_is_case_sensitive()
    {
        var result = Validate(Response(status: "NO_WATER"));

        result.ShouldBe(ValidationResult.InvalidStatus);
    }

    [Fact]
    public void Evidence_with_other_case_is_rejected()
    {
        var result = Validate(Response(quote: "Воду отключили"));

        result.ShouldBe(ValidationResult.EvidenceNotSubstring);
    }

    [Fact]
    public void Evidence_with_extra_space_is_rejected()
    {
        var result = Validate(Response(quote: "воду  отключили"));

        result.ShouldBe(ValidationResult.EvidenceNotSubstring);
    }

    [Fact]
    public void Empty_evidence_is_rejected()
    {
        var result = Validate(Response(quote: string.Empty));

        result.ShouldBe(ValidationResult.EvidenceNotSubstring);
    }

    [Fact]
    public void Whitespace_only_evidence_is_rejected()
    {
        var result = Validate(Response(quote: "   "));

        result.ShouldBe(ValidationResult.EvidenceNotSubstring);
    }

    [Fact]
    public void Evidence_absent_from_message_is_rejected()
    {
        var result = Validate(Response(quote: "воду дадут завтра"));

        result.ShouldBe(ValidationResult.EvidenceNotSubstring);
    }

    [Fact]
    public void Crlf_is_collapsed_in_message_and_quote()
    {
        const string message = "Объявление:\r\nводы не будет до вечера";
        const string quote = "Объявление:\nводы не будет";

        var result = ExtractionValidator.Validate(
            Response(quote: quote),
            message,
            StatusCodes,
            SourceObjectIds,
            linkByText: false,
            multipleObjects: true);

        result.ShouldBe(ValidationResult.Valid);
    }

    [Fact]
    public void Crlf_is_collapsed_in_quote_too()
    {
        const string message = "Объявление:\nводы не будет до вечера";
        const string quote = "Объявление:\r\nводы не будет";

        var result = ExtractionValidator.Validate(
            Response(quote: quote),
            message,
            StatusCodes,
            SourceObjectIds,
            linkByText: false,
            multipleObjects: true);

        result.ShouldBe(ValidationResult.Valid);
    }

    [Fact]
    public void Single_line_feeds_are_not_collapsed()
    {
        // Нормализуется только \r\n; одиночный \r остаётся как есть — иначе проверка теряет смысл.
        const string message = "Объявление:\rводы не будет";
        const string quote = "Объявление:\nводы не будет";

        var result = ExtractionValidator.Validate(
            Response(quote: quote),
            message,
            StatusCodes,
            SourceObjectIds,
            linkByText: false,
            multipleObjects: false);

        result.ShouldBe(ValidationResult.EvidenceNotSubstring);
    }

    [Fact]
    public void Null_object_with_link_by_text_and_multiple_objects_is_object_not_found()
    {
        var result = Validate(Response(objectId: null), linkByText: true, multipleObjects: true);

        result.ShouldBe(ValidationResult.ObjectNotFound);
    }

    [Fact]
    public void Unknown_object_with_link_by_text_and_multiple_objects_is_object_not_found()
    {
        var result = Validate(
            Response(objectId: Guid.NewGuid().ToString("D")),
            linkByText: true,
            multipleObjects: true);

        result.ShouldBe(ValidationResult.ObjectNotFound);
    }

    [Fact]
    public void Non_guid_object_id_is_object_not_found()
    {
        var result = Validate(Response(objectId: "дом на углу"), linkByText: true, multipleObjects: true);

        result.ShouldBe(ValidationResult.ObjectNotFound);
    }

    [Fact]
    public void Known_object_passes()
    {
        var result = Validate(
            Response(objectId: SecondObject.ToString("D")),
            linkByText: true,
            multipleObjects: true);

        result.ShouldBe(ValidationResult.Valid);
    }

    [Fact]
    public void Object_is_ignored_when_link_by_text_is_off()
    {
        var result = Validate(
            Response(objectId: Guid.NewGuid().ToString("D")),
            linkByText: false,
            multipleObjects: true);

        result.ShouldBe(ValidationResult.Valid);
    }

    [Fact]
    public void Object_is_ignored_when_source_has_single_object()
    {
        var result = Validate(
            Response(objectId: Guid.NewGuid().ToString("D")),
            linkByText: true,
            multipleObjects: false);

        result.ShouldBe(ValidationResult.Valid);
    }

    [Fact]
    public void Not_relevant_response_needs_no_validation()
    {
        var response = new ParsedLlmResponse(false, null, null, 0d, string.Empty);

        var result = ExtractionValidator.Validate(
            response,
            Message,
            StatusCodes,
            SourceObjectIds,
            linkByText: true,
            multipleObjects: true);

        result.ShouldBe(ValidationResult.Valid);
    }

    private static ValidationResult Validate(
        ParsedLlmResponse response,
        bool linkByText = false,
        bool multipleObjects = false) =>
        ExtractionValidator.Validate(response, Message, StatusCodes, SourceObjectIds, linkByText, multipleObjects);

    private static ParsedLlmResponse Response(
        string? status = "no_water",
        string quote = "воду отключили",
        string? objectId = null) =>
        new(true, status, objectId, 0.92, quote);
}
