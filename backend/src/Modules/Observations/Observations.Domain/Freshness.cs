namespace Observations.Domain;

/// <summary>
/// Свежесть победившего наблюдения: используется для визуального «затухания»
/// статуса на карте. <see cref="Stale"/> означает, что актуальных наблюдений нет.
/// </summary>
public enum Freshness
{
    /// <summary>Возраст меньше доли <c>freshRatio</c> от таймаута источника.</summary>
    Fresh,

    /// <summary>Возраст от доли <c>freshRatio</c> до истечения таймаута источника.</summary>
    Aging,

    /// <summary>Актуальных наблюдений нет — статус «неизвестно».</summary>
    Stale,
}
