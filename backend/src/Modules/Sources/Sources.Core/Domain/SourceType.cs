namespace Sources.Core.Domain;

/// <summary>
/// Тип источника (таблица <c>source_types</c>): расширяемый реестр адаптеров.
/// Начальные записи — <c>telegram_chat</c> («Telegram-чат») и <c>max_chat</c> («MAX-чат»)
/// из <c>db/seed.sql</c>; новый тип добавляется адаптером без изменения остального кода.
/// </summary>
internal sealed class SourceType
{
    /// <summary>Код типа: <c>telegram_chat</c>, <c>max_chat</c>.</summary>
    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public bool IsEnabled { get; set; } = true;
}
