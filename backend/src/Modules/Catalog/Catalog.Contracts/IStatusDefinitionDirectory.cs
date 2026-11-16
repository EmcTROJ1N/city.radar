namespace Catalog.Contracts;

/// <summary>
/// Чтение библиотеки статусов категории другими модулями. Прямая ссылка на
/// <c>Catalog.Core</c> запрещена: модуль источников берёт отсюда записи
/// <c>status_definitions</c>, чтобы создать одноимённые статусы системного источника «Сайт».
/// </summary>
public interface IStatusDefinitionDirectory
{
    /// <summary>
    /// Статусы справочника категории в порядке «серьёзности» (<c>severity</c>), затем по названию.
    /// Пустой список — у категории нет статусов.
    /// </summary>
    Task<IReadOnlyList<StatusDefinitionInfo>> GetByCategoryAsync(Guid categoryId, CancellationToken ct);
}

/// <summary>Запись справочника статусов (таблица <c>status_definitions</c>).</summary>
/// <param name="Id">Идентификатор записи справочника.</param>
/// <param name="Code">Код статуса.</param>
/// <param name="Name">Название статуса.</param>
/// <param name="Color">Ключ палитры оформления.</param>
/// <param name="Icon">Ключ иконки.</param>
/// <param name="Severity">Порядок «серьёзности»; используется и как порядок показа.</param>
public sealed record StatusDefinitionInfo(
    Guid Id,
    string Code,
    string Name,
    string Color,
    string Icon,
    int Severity);
