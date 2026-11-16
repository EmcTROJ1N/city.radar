namespace BuildingBlocks.Web;

/// <summary>
/// Страница курсорной пагинации по контракту (схемы <c>*Page</c>): элементы и курсор следующей
/// страницы. На последней странице <see cref="NextCursor"/> равен <c>null</c>.
/// Смещения и общего количества контракт не предусматривает.
/// </summary>
/// <typeparam name="T">Тип элемента страницы.</typeparam>
/// <param name="Items">Элементы текущей страницы.</param>
/// <param name="NextCursor">Курсор следующей страницы или <c>null</c>.</param>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);
