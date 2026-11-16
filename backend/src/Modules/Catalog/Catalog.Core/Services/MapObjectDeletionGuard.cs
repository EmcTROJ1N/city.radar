using Catalog.Contracts;

namespace Catalog.Core.Services;

/// <summary>
/// Реализация <see cref="IMapObjectDeletionGuard"/> по умолчанию: таблицы <c>observations</c>
/// пока нет (она создаётся позже), поэтому наблюдений у фиксаций быть не может.
/// </summary>
/// <remarks>
/// TODO: модуль наблюдений заменит регистрацию этой реализации на проверку
/// <c>exists (select 1 from observations where map_object_id = @id)</c>. Каталог при этом
/// не меняется — он всегда спрашивает <see cref="IMapObjectDeletionGuard"/>.
/// </remarks>
internal sealed class MapObjectDeletionGuard : IMapObjectDeletionGuard
{
    public Task<bool> HasObservationsAsync(Guid mapObjectId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(false);
    }
}
