using BuildingBlocks.Web;

using Shouldly;

namespace Api.IntegrationTests.Common;

/// <summary>
/// Проверка текста перед записью в PostgreSQL: нулевой символ и одиночные суррогаты
/// недопустимы. Тесты чистые, БД не нужна.
/// </summary>
public sealed class TextValidatorTests
{
    [Theory]
    [InlineData("Вода")]
    [InlineData("Лёд")]
    [InlineData("объём")]
    [InlineData("Mayak-2026")]
    [InlineData("Дом культуры, 15/2")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\ud83d\udca7")]
    public void Valid_text_accepted(string? text)
    {
        TextValidator.IsValid(text).ShouldBeTrue();
    }

    [Fact]
    public void Unstorable_text_rejected()
    {
        TextValidator.IsValid("Вода\0").ShouldBeFalse();
        TextValidator.IsValid(new string([(char)0xD800])).ShouldBeFalse();
        TextValidator.IsValid(new string([(char)0xDC00])).ShouldBeFalse();
    }

    [Fact]
    public void Error_list_contains_field_names()
    {
        var errors = TextValidator.Validate(
            ("name", "Вода"),
            ("address", "ул. Мира, 1"),
            ("description", null),
            ("statusName", "Вода\0"));

        errors.Select(error => error.Field).ShouldBe(["statusName"]);
        errors.ShouldAllBe(error =>
            error.Message.Contains("нулевой символ", StringComparison.Ordinal));
    }
}
