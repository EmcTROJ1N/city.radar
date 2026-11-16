namespace Sources.Contracts;

/// <summary>
/// Чтение реестра источников другими модулями: ядро
/// наблюдений берёт политику источника для расчёта <c>expires_at</c>, а каталог проверяет,
/// что привязываемый источник существует. Прямая ссылка на <c>Sources.Core</c> запрещена
///.
/// </summary>
public interface ISourceDirectory
{
    /// <summary>
    /// Политика источника: таймаут актуальности, порог уверенности, тип адаптера и режим
    /// определения фиксации по тексту. <c>null</c>, если источника или политики нет.
    /// </summary>
    Task<SourcePolicyInfo?> GetPolicyAsync(Guid sourceId, CancellationToken ct);

    /// <summary>
    /// Статусы, которые может «докладывать» источник, в порядке показа
    /// (<c>sortOrder</c>, затем название). Пустой список — источника нет или у него нет статусов.
    /// </summary>
    Task<IReadOnlyList<SourceStatusInfo>> GetStatusesAsync(Guid sourceId, CancellationToken ct);

    /// <summary>Существует ли источник и включён ли он.</summary>
    Task<bool> IsEnabledAsync(Guid sourceId, CancellationToken ct);

    /// <summary>Существует ли источник; удалённый источник не существует.</summary>
    Task<bool> ExistsAsync(Guid sourceId, CancellationToken ct);

    /// <summary>
    /// Идентификатор системного источника «Сайт» категории; <c>null</c> — «Сайта» нет.
    /// Источник доступен фиксациям категории неявно, без записи в <c>map_object_sources</c>.
    /// Возвращается и выключенный «Сайт»: запрет отметок по нему проверяется отдельно.
    /// </summary>
    Task<Guid?> GetSiteSourceIdAsync(Guid categoryId, CancellationToken ct);
}

/// <summary>Политика источника (таблица <c>source_policies</c> и поля <c>sources</c>).</summary>
/// <param name="SourceId">Идентификатор источника.</param>
/// <param name="Timeout">Таймаут актуальности наблюдений источника.</param>
/// <param name="ConfidenceThreshold">Порог уверенности LLM; <c>null</c> — значение по умолчанию.</param>
/// <param name="SourceType">Код типа адаптера: <c>telegram_chat</c>, <c>max_chat</c>.</param>
/// <param name="LinkByText">Режим «определять фиксацию по тексту» (по умолчанию включён).</param>
public sealed record SourcePolicyInfo(
    Guid SourceId,
    TimeSpan Timeout,
    float? ConfidenceThreshold,
    string SourceType,
    bool LinkByText);

/// <summary>Статус, который может «докладывать» источник (таблица <c>source_statuses</c>).</summary>
/// <param name="Id">Идентификатор статуса источника.</param>
/// <param name="Code">Код, уникальный внутри источника.</param>
/// <param name="Name">Название статуса.</param>
/// <param name="Description">Подсказка для LLM.</param>
/// <param name="Color">Ключ палитры оформления.</param>
/// <param name="Icon">Ключ иконки.</param>
public sealed record SourceStatusInfo(
    Guid Id,
    string Code,
    string Name,
    string Description,
    string Color,
    string Icon);
