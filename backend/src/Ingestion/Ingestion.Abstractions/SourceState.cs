namespace Ingestion.Abstractions;

/// <summary>
/// Состояние источника. Значения точно совпадают с check-констрейнтом
/// колонки <c>sources.state</c> в <c>db/schema.sql</c>; в БД они хранятся в snake_case
/// (см. <see cref="SourceStateExtensions.ToDatabaseValue"/>).
/// </summary>
/// <remarks>
/// Схема допускает только шесть состояний. Бан или разлогин аккаунта Telegram передаются как
/// <see cref="AuthError"/>, недоступность прокси — как <see cref="Error"/>: отдельных значений
/// для них в БД нет.
/// </remarks>
public enum SourceState
{
    /// <summary>Состояние ещё не определялось.</summary>
    Unknown,

    /// <summary>Источник работает, сообщения поступают.</summary>
    Ok,

    /// <summary>Источник доступен, но давно не присылал сообщений.</summary>
    Silent,

    /// <summary>Telegram требует подождать (FloodWait); деталь содержит срок ожидания.</summary>
    FloodWait,

    /// <summary>Ошибка авторизации: сессия недействительна, аккаунт разлогинен или забанен.</summary>
    AuthError,

    /// <summary>Прочая ошибка адаптера, в том числе недоступный прокси.</summary>
    Error,
}
