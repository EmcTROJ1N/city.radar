namespace Observations.Domain;

/// <summary>
/// Тип источника наблюдения. Пользовательские отметки (<see cref="Manual"/>,
/// <see cref="Moderator"/>) и наблюдения из источников данных (<see cref="Telegram"/>,
/// <see cref="Max"/>) при расчёте статуса равнозначны.
/// </summary>
public enum SourceKind
{
    /// <summary>Ручная отметка пользователя.</summary>
    Manual,

    /// <summary>Отметка модератора; приоритета перед остальными не имеет.</summary>
    Moderator,

    /// <summary>Наблюдение из Telegram-чата.</summary>
    Telegram,

    /// <summary>Наблюдение из MAX-чата.</summary>
    Max,
}
