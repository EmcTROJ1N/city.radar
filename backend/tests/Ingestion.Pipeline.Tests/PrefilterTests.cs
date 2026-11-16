using System.Diagnostics;

using Shouldly;

namespace Ingestion.Pipeline.Tests;

/// <summary>
/// Тесты префильтра (шаг 1): ключевые слова без учёта регистра, regex с таймаутом,
/// «любое из правил», отсутствие конфигурации пропускает всё. Невалидное выражение и таймаут
/// не бросают исключений и не подвешивают обработку сообщений (ReDoS).
/// </summary>
public sealed class PrefilterTests
{
    [Fact]
    public void Without_config_everything_passes()
    {
        Prefilter.Passes("любой текст", null).ShouldBeTrue();
    }

    [Fact]
    public void Empty_config_passes_everything()
    {
        Prefilter.Passes("любой текст", new PrefilterConfig(null, null)).ShouldBeTrue();
        Prefilter.Passes("любой текст", new PrefilterConfig([], [])).ShouldBeTrue();
    }

    [Fact]
    public void Keyword_matches_without_case()
    {
        var config = new PrefilterConfig(["ВОДА"], null);

        Prefilter.Passes("Вода пропала во всём районе", config).ShouldBeTrue();
    }

    [Fact]
    public void Keyword_that_is_absent_rejects()
    {
        var config = new PrefilterConfig(["вода"], null);

        Prefilter.Passes("Отключили электричество", config).ShouldBeFalse();
    }

    [Fact]
    public void Regex_match_passes()
    {
        var config = new PrefilterConfig(null, [@"\bнет\b"]);

        Prefilter.Passes("Сегодня воды нет", config).ShouldBeTrue();
    }

    [Fact]
    public void Regex_without_match_rejects()
    {
        var config = new PrefilterConfig(null, [@"\bнет\b"]);

        Prefilter.Passes("Вода есть", config).ShouldBeFalse();
    }

    [Fact]
    public void Keyword_or_regex_is_enough()
    {
        var config = new PrefilterConfig(["электричество"], [@"\bнет\b"]);

        Prefilter.Passes("Сегодня воды нет", config).ShouldBeTrue();
    }

    [Fact]
    public void Invalid_regex_is_treated_as_no_match()
    {
        var broken = new PrefilterConfig(null, ["("]);

        Prefilter.Passes("Вода есть", broken).ShouldBeFalse();
        Prefilter.Passes("Вода есть", new PrefilterConfig(["вода"], ["("])).ShouldBeTrue();
    }

    [Fact]
    public void Prefilter_regex_timeout_does_not_hang()
    {
        var text = new string('a', 100) + "!";
        var config = new PrefilterConfig(null, [@"(a+)+$"]);

        var stopwatch = Stopwatch.StartNew();
        var passed = Prefilter.Passes(text, config);
        stopwatch.Stop();

        passed.ShouldBeFalse();
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }
}
