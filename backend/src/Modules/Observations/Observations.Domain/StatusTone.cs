namespace Observations.Domain;

/// <summary>
/// Смысл статуса наблюдения для сводки по зоне: берётся из цвета статуса источника
/// (<c>ok</c> → <see cref="Positive"/>, <c>none</c> → <see cref="Negative"/>, остальные → <see cref="Limited"/>).
/// </summary>
public enum StatusTone
{
    /// <summary>Ресурс есть.</summary>
    Positive,

    /// <summary>Ресурс есть с ограничениями: слабый напор, только нижние этажи, по графику и т. п.</summary>
    Limited,

    /// <summary>Ресурса нет или авария.</summary>
    Negative,
}
