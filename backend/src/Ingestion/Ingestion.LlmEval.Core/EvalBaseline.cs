using System.Globalization;
using System.Text.Json;

namespace Ingestion.LlmEval.Core;

/// <summary>
/// Сведения из предыдущего JSON-отчёта, нужные для регрессионной проверки.
/// </summary>
/// <param name="SchemaVersion">Версия формата отчёта.</param>
/// <param name="Model">Модель предыдущего прогона; <c>null</c>, если не записана.</param>
/// <param name="Precision">Precision предыдущего прогона; <c>null</c>, если публикаций не было.</param>
/// <param name="DatasetSize">Размер набора предыдущего прогона.</param>
public sealed record EvalBaselineSnapshot(
    string SchemaVersion,
    string? Model,
    double? Precision,
    int DatasetSize);

/// <summary>
/// Результат сравнения текущего прогона с baseline: регрессия — это падение precision
/// ниже абсолютного порога или более чем на 2 п.п. относительно предыдущего прогона.
/// </summary>
/// <param name="BaselineModel">Модель baseline; <c>null</c>, если неизвестна.</param>
/// <param name="BaselinePrecision">Precision baseline.</param>
/// <param name="CurrentPrecision">Precision текущего прогона.</param>
/// <param name="DropPercentagePoints">Просадка в процентных пунктах (baseline − текущая).</param>
/// <param name="MinPrecision">Абсолютный порог precision.</param>
/// <param name="BelowMinPrecision">Текущая precision ниже абсолютного порога (или не определена).</param>
/// <param name="Regressed">Просадка относительно baseline больше допустимых 2 п.п.</param>
/// <param name="Failed">Итог: регрессионный прогон завершается кодом 1.</param>
/// <param name="Summary">Пояснение для человека.</param>
public sealed record BaselineComparison(
    string? BaselineModel,
    double? BaselinePrecision,
    double? CurrentPrecision,
    double DropPercentagePoints,
    double MinPrecision,
    bool BelowMinPrecision,
    bool Regressed,
    bool Failed,
    string Summary)
{
    /// <summary>Код возврата консольного раннера: 1 — регрессия, 0 — успех.</summary>
    public int ExitCode => Failed ? 1 : 0;
}

/// <summary>
/// Регрессионный режим (<c>--baseline</c> и <c>--min-precision</c>): сравнение текущего отчёта
/// с предыдущим. Код 1 возвращается, если precision ниже <c>--min-precision</c> (по умолчанию 0.95)
/// или упала более чем на 2 п.п. относительно baseline.
/// </summary>
public static class EvalBaseline
{
    /// <summary>Абсолютный порог precision по умолчанию для регрессионного прогона.</summary>
    public const double DefaultMinPrecision = 0.95;

    /// <summary>Допустимая просадка precision относительно baseline, процентные пункты.</summary>
    public const double MaxDropPercentagePoints = 2.0;

    private const double Epsilon = 1e-9;

    /// <summary>
    /// Читает baseline из JSON-отчёта. Полная десериализация не нужна: для сравнения достаточно
    /// версии формата, модели, precision и размера набора.
    /// </summary>
    /// <param name="json">Содержимое предыдущего JSON-отчёта.</param>
    /// <exception cref="EvalConfigurationException">JSON или версия формата некорректны.</exception>
    public static EvalBaselineSnapshot ReadSnapshot(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new EvalConfigurationException(
                $"Отчёт baseline не является корректным JSON: {ex.Message}",
                ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new EvalConfigurationException("Отчёт baseline должен быть JSON-объектом.");
            }

            var schemaVersion = ReadString(root, "schemaVersion") ?? string.Empty;
            if (!string.Equals(schemaVersion, EvalReport.CurrentSchemaVersion, StringComparison.Ordinal))
            {
                throw new EvalConfigurationException(
                    $"Несовместимая версия отчёта baseline: \"{schemaVersion}\"; " +
                    $"ожидается \"{EvalReport.CurrentSchemaVersion}\".");
            }

            var model = ReadStringInObject(root, "run", "model");
            var datasetSize = ReadIntInObject(root, "run", "datasetSize") ?? 0;

            double? precision = null;
            if (root.TryGetProperty("metrics", out var metrics) &&
                metrics.ValueKind == JsonValueKind.Object &&
                metrics.TryGetProperty("precision", out var precisionElement) &&
                precisionElement.ValueKind == JsonValueKind.Number &&
                precisionElement.TryGetDouble(out var value))
            {
                precision = value;
            }

            return new EvalBaselineSnapshot(schemaVersion, model, precision, datasetSize);
        }
    }

    /// <summary>Сравнивает текущий отчёт с baseline.</summary>
    /// <param name="current">Текущий отчёт.</param>
    /// <param name="baseline">Данные предыдущего отчёта.</param>
    /// <param name="minPrecision">Абсолютный порог precision; должен быть в диапазоне 0..1.</param>
    /// <exception cref="ArgumentOutOfRangeException">Порог вне диапазона 0..1.</exception>
    public static BaselineComparison Compare(
        EvalReport current,
        EvalBaselineSnapshot baseline,
        double minPrecision)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(baseline);

        if (!double.IsFinite(minPrecision) || minPrecision is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minPrecision),
                minPrecision,
                "Порог precision должен быть числом от 0 до 1.");
        }

        var currentPrecision = current.Metrics.Precision;
        var belowMin = currentPrecision is null || currentPrecision.Value < minPrecision - Epsilon;

        var drop = baseline.Precision is not null && currentPrecision is not null
            ? (baseline.Precision.Value - currentPrecision.Value) * 100d
            : 0d;

        var regressed = baseline.Precision is not null &&
                        currentPrecision is not null &&
                        drop > MaxDropPercentagePoints + Epsilon;

        var failed = belowMin || regressed;

        var summary = BuildSummary(
            currentPrecision,
            baseline.Precision,
            belowMin,
            regressed,
            minPrecision,
            drop);

        return new BaselineComparison(
            baseline.Model,
            baseline.Precision,
            currentPrecision,
            Math.Round(drop, 2),
            minPrecision,
            belowMin,
            regressed,
            failed,
            summary);
    }

    private static string BuildSummary(
        double? current,
        double? baseline,
        bool belowMin,
        bool regressed,
        double minPrecision,
        double drop)
    {
        var parts = new List<string>(2);

        if (belowMin)
        {
            parts.Add(
                $"precision {(current is null ? "не определена (нет опубликованных наблюдений)" : current.Value.ToString("0.000", CultureInfo.InvariantCulture))} " +
                $"ниже порога {minPrecision.ToString("0.000", CultureInfo.InvariantCulture)}");
        }

        if (regressed)
        {
            parts.Add(
                $"precision упала на {drop.ToString("0.00", CultureInfo.InvariantCulture)} п.п. " +
                $"относительно baseline {baseline!.Value.ToString("0.000", CultureInfo.InvariantCulture)} " +
                $"(порог {MaxDropPercentagePoints.ToString("0.00", CultureInfo.InvariantCulture)} п.п.)");
        }

        if (parts.Count == 0)
        {
            return baseline is null
                ? "precision не определена в baseline — сравнение с предыдущим прогоном пропущено."
                : $"precision {current!.Value.ToString("0.000", CultureInfo.InvariantCulture)} не ниже порога " +
                  $"{minPrecision.ToString("0.000", CultureInfo.InvariantCulture)} и просадка " +
                  $"{drop.ToString("0.00", CultureInfo.InvariantCulture)} п.п. допустима.";
        }

        return string.Join("; ", parts) + ".";
    }

    private static string? ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string? ReadStringInObject(JsonElement root, string objectProperty, string property) =>
        root.TryGetProperty(objectProperty, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? ReadString(nested, property)
            : null;

    private static int? ReadIntInObject(JsonElement root, string objectProperty, string property) =>
        root.TryGetProperty(objectProperty, out var nested) &&
        nested.ValueKind == JsonValueKind.Object &&
        nested.TryGetProperty(property, out var element) &&
        element.ValueKind == JsonValueKind.Number &&
        element.TryGetInt32(out var value)
            ? value
            : null;
}
