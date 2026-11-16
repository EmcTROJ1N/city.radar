namespace Ingestion.Abstractions;

/// <summary>
/// Фабрика адаптеров источника: реестр типов расширяется регистрацией новой
/// фабрики, без правки supervisor-а и остального кода.
/// </summary>
/// <remarks>
/// Один тип источника может поддерживаться несколькими способами чтения (например,
/// <c>telegram_chat</c> — юзербот MTProto и Telegram Bot API). Такие фабрики различаются
/// <see cref="CanHandle"/>: supervisor выбирает первую подходящую.
/// </remarks>
public interface ISourceAdapterFactory
{
    /// <summary>Код типа источника, совпадающий с <c>source_types.code</c> (например, <c>telegram_chat</c>).</summary>
    string SourceType { get; }

    /// <summary>
    /// Может ли фабрика обслуживать источник. Реализация по умолчанию сравнивает
    /// <see cref="SourceType"/>; фабрика с несколькими режимами переопределяет метод и смотрит
    /// на настройки (например, <c>settings.mode</c>).
    /// </summary>
    /// <param name="config">Снимок источника.</param>
    bool CanHandle(SourceRuntimeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return string.Equals(SourceType, config.SourceType, StringComparison.Ordinal);
    }

    /// <summary>Создаёт адаптер для источника с указанной конфигурацией.</summary>
    /// <param name="config">Снимок настроек источника, секрет сессии и прокси.</param>
    /// <exception cref="NotSupportedException">
    /// Фабрика не обслуживает этот источник (см. <see cref="CanHandle"/>).
    /// </exception>
    IDataSource Create(SourceRuntimeConfig config);
}
