using System.Globalization;
using System.Text.Json;

using IdentityAccess.Contracts;

namespace IdentityAccess.Core.Settings;

/// <summary>
/// Реестр ключей <c>global_settings</c>: имя ключа в БД (snake_case), имя поля в контракте
/// (<c>GlobalSettings</c> / <c>GlobalSettingsPatch</c>, camelCase), значение по умолчанию и схема
/// значения (тип и диапазон). Значения по умолчанию совпадают с <c>db/seed.sql</c>.
/// </summary>
internal static class GlobalSettingsRegistry
{
    private const int DefaultObservationRatePerHour = 20;
    private const int DefaultCommentMaxLength = 500;
    private const double DefaultConfidenceThreshold = 0.7;
    private const int DefaultAnomalyWindowMinutes = 5;
    private const int DefaultAnomalyThreshold = 30;
    private const double DefaultFreshnessFreshRatio = 0.5;

    private static readonly SettingDefinition[] Definitions =
    [
        Integer(
            "observation_rate_per_hour",
            "observationRatePerHour",
            snapshot => snapshot.ObservationRatePerHour,
            (snapshot, value) => snapshot with { ObservationRatePerHour = value },
            minimum: 1),
        Integer(
            "comment_max_length",
            "commentMaxLength",
            snapshot => snapshot.CommentMaxLength,
            (snapshot, value) => snapshot with { CommentMaxLength = value },
            minimum: 1,
            maximum: 5000),
        Number(
            "confidence_threshold_default",
            "confidenceThresholdDefault",
            snapshot => snapshot.ConfidenceThresholdDefault,
            (snapshot, value) => snapshot with { ConfidenceThresholdDefault = value },
            minimum: 0,
            maximum: 1),
        Integer(
            "anomaly_window_minutes",
            "anomalyWindowMinutes",
            snapshot => snapshot.AnomalyWindowMinutes,
            (snapshot, value) => snapshot with { AnomalyWindowMinutes = value },
            minimum: 1),
        Integer(
            "anomaly_threshold",
            "anomalyThreshold",
            snapshot => snapshot.AnomalyThreshold,
            (snapshot, value) => snapshot with { AnomalyThreshold = value },
            minimum: 1),
        Number(
            "freshness_fresh_ratio",
            "freshnessFreshRatio",
            snapshot => snapshot.FreshnessFreshRatio,
            (snapshot, value) => snapshot with { FreshnessFreshRatio = value },
            minimum: 0,
            maximum: 1,
            exclusiveBounds: true),
        Uuid(
            "proxy_secret_id",
            "proxySecretId",
            snapshot => snapshot.ProxySecretId,
            (snapshot, value) => snapshot with { ProxySecretId = value }),
    ];

    /// <summary>Все настройки в порядке схемы контракта.</summary>
    public static IReadOnlyList<SettingDefinition> All { get; } = Array.AsReadOnly(Definitions);

    /// <summary>Значения по умолчанию: их получает GET, если в <c>global_settings</c> нет ключа.</summary>
    public static GlobalSettingsSnapshot Defaults { get; } = new(
        DefaultObservationRatePerHour,
        DefaultCommentMaxLength,
        DefaultConfidenceThreshold,
        DefaultAnomalyWindowMinutes,
        DefaultAnomalyThreshold,
        DefaultFreshnessFreshRatio,
        ProxySecretId: null);

    /// <summary>Настройка по имени поля контракта; <c>null</c> — неизвестное поле (422).</summary>
    public static SettingDefinition? FindByField(string field) =>
        Definitions.FirstOrDefault(definition => string.Equals(definition.Field, field, StringComparison.Ordinal));

    private static SettingDefinition<Guid?> Uuid(
        string key,
        string field,
        Func<GlobalSettingsSnapshot, Guid?> read,
        Func<GlobalSettingsSnapshot, Guid?, GlobalSettingsSnapshot> apply) =>
        new(key, field, read, apply, ParseUuid);

    private static SettingDefinition<int> Integer(
        string key,
        string field,
        Func<GlobalSettingsSnapshot, int> read,
        Func<GlobalSettingsSnapshot, int, GlobalSettingsSnapshot> apply,
        int? minimum = null,
        int? maximum = null) =>
        new(
            key,
            field,
            read,
            apply,
            json => ParseInteger(json, minimum, maximum));

    private static SettingDefinition<double> Number(
        string key,
        string field,
        Func<GlobalSettingsSnapshot, double> read,
        Func<GlobalSettingsSnapshot, double, GlobalSettingsSnapshot> apply,
        double minimum,
        double maximum,
        bool exclusiveBounds = false) =>
        new(
            key,
            field,
            read,
            apply,
            json => ParseNumber(json, minimum, maximum, exclusiveBounds));

    private static SettingParseResult<int> ParseInteger(JsonElement json, int? minimum, int? maximum)
    {
        if (json.ValueKind != JsonValueKind.Number || !json.TryGetInt32(out var value))
        {
            return SettingParseResult<int>.Failure("Ожидается целое число");
        }

        if (minimum is { } min && value < min)
        {
            return SettingParseResult<int>.Failure($"Значение должно быть не меньше {min.ToString(CultureInfo.InvariantCulture)}");
        }

        if (maximum is { } max && value > max)
        {
            return SettingParseResult<int>.Failure($"Значение должно быть не больше {max.ToString(CultureInfo.InvariantCulture)}");
        }

        return SettingParseResult<int>.Success(value);
    }

    private static SettingParseResult<double> ParseNumber(
        JsonElement json,
        double minimum,
        double maximum,
        bool exclusiveBounds)
    {
        if (json.ValueKind != JsonValueKind.Number || !json.TryGetDouble(out var value))
        {
            return SettingParseResult<double>.Failure("Ожидается число");
        }

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return SettingParseResult<double>.Failure("Ожидается конечное число");
        }

        var minText = minimum.ToString(CultureInfo.InvariantCulture);
        var maxText = maximum.ToString(CultureInfo.InvariantCulture);

        if (exclusiveBounds)
        {
            if (value <= minimum || value >= maximum)
            {
                return SettingParseResult<double>.Failure(
                    $"Значение должно быть больше {minText} и меньше {maxText}");
            }

            return SettingParseResult<double>.Success(value);
        }

        if (value < minimum || value > maximum)
        {
            return SettingParseResult<double>.Failure($"Значение должно быть от {minText} до {maxText}");
        }

        return SettingParseResult<double>.Success(value);
    }

    private static SettingParseResult<Guid?> ParseUuid(JsonElement json)
    {
        if (json.ValueKind == JsonValueKind.Null)
        {
            return SettingParseResult<Guid?>.Success(null);
        }

        if (json.ValueKind == JsonValueKind.String && Guid.TryParse(json.GetString(), out var value))
        {
            return SettingParseResult<Guid?>.Success(value);
        }

        return SettingParseResult<Guid?>.Failure("Ожидается UUID или null");
    }
}

/// <summary>Схема значения настройки: как оно выглядит в снимке, как применяется и как валидируется.</summary>
internal abstract class SettingDefinition(string key, string field)
{
    /// <summary>Ключ в таблице <c>global_settings</c> (snake_case).</summary>
    public string Key { get; } = key;

    /// <summary>Имя поля в контракте (camelCase).</summary>
    public string Field { get; } = field;

    /// <summary>Значение поля в снимке по умолчанию.</summary>
    public abstract object? Read(GlobalSettingsSnapshot snapshot);

    /// <summary>Возвращает копию снимка с применённым значением.</summary>
    public abstract GlobalSettingsSnapshot Apply(GlobalSettingsSnapshot snapshot, object? value);

    /// <summary>Проверяет JSON-значение по схеме ключа (тип и диапазон).</summary>
    public abstract bool TryParse(JsonElement json, out object? value, out string? error);
}

internal sealed class SettingDefinition<T>(
    string key,
    string field,
    Func<GlobalSettingsSnapshot, T> read,
    Func<GlobalSettingsSnapshot, T, GlobalSettingsSnapshot> apply,
    Func<JsonElement, SettingParseResult<T>> parse)
    : SettingDefinition(key, field)
{
    public override object? Read(GlobalSettingsSnapshot snapshot) => read(snapshot);

    public override GlobalSettingsSnapshot Apply(GlobalSettingsSnapshot snapshot, object? value) =>
        apply(snapshot, (T)value!);

    public override bool TryParse(JsonElement json, out object? value, out string? error)
    {
        var result = parse(json);

        value = result.Ok ? result.Value : (object?)null;
        error = result.Error;

        return result.Ok;
    }
}

/// <summary>Результат валидации значения настройки: значение либо текст ошибки для <c>errors[]</c>.</summary>
internal readonly record struct SettingParseResult<T>(bool Ok, T? Value, string? Error)
{
    public static SettingParseResult<T> Success(T value) => new(true, value, null);

    public static SettingParseResult<T> Failure(string error) => new(false, default, error);
}
