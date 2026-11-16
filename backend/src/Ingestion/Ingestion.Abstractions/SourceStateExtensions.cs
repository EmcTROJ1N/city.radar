namespace Ingestion.Abstractions;

/// <summary>
/// Преобразование <see cref="SourceState"/> в значение колонки <c>sources.state</c> и обратно.
/// </summary>
public static class SourceStateExtensions
{
    /// <summary>Возвращает значение для БД в snake_case (например, <c>flood_wait</c>).</summary>
    /// <param name="state">Состояние источника.</param>
    /// <exception cref="ArgumentOutOfRangeException">Значение перечисления неизвестно.</exception>
    public static string ToDatabaseValue(this SourceState state) => state switch
    {
        SourceState.Unknown => "unknown",
        SourceState.Ok => "ok",
        SourceState.Silent => "silent",
        SourceState.FloodWait => "flood_wait",
        SourceState.AuthError => "auth_error",
        SourceState.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Неизвестное состояние источника."),
    };

    /// <summary>Разбирает значение колонки <c>sources.state</c>.</summary>
    /// <param name="value">Значение в snake_case.</param>
    /// <exception cref="ArgumentOutOfRangeException">Значение не входит в допустимый набор.</exception>
    public static SourceState ParseDatabaseValue(string value) =>
        TryParseDatabaseValue(value, out var state)
            ? state
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Неизвестное состояние источника.");

    /// <summary>Пробует разобрать значение колонки <c>sources.state</c>.</summary>
    /// <param name="value">Значение в snake_case; <c>null</c> считается неизвестным.</param>
    /// <param name="state">Распознанное состояние или <see cref="SourceState.Unknown"/>.</param>
    public static bool TryParseDatabaseValue(string? value, out SourceState state)
    {
        switch (value)
        {
            case "unknown":
                state = SourceState.Unknown;
                return true;
            case "ok":
                state = SourceState.Ok;
                return true;
            case "silent":
                state = SourceState.Silent;
                return true;
            case "flood_wait":
                state = SourceState.FloodWait;
                return true;
            case "auth_error":
                state = SourceState.AuthError;
                return true;
            case "error":
                state = SourceState.Error;
                return true;
            default:
                state = SourceState.Unknown;
                return false;
        }
    }
}
