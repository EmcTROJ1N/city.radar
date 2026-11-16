using BuildingBlocks.Persistence;
using BuildingBlocks.Web;

using Catalog.Core.Domain;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace Catalog.Core.Endpoints;

/// <summary>
/// Общие проверки и сохранение изменений каталога. Нарушение уникального индекса и внешнего
/// ключа ловится по SQLSTATE (<c>23505</c>/<c>23503</c>), а не только pre-check: гонка двух
/// запросов не должна превращаться в 500.
/// </summary>
internal static class CatalogPersistence
{
    /// <summary>Существует ли категория: несуществующий <c>categoryId</c> — 422 по контракту.</summary>
    public static Task<bool> CategoryExistsAsync(MayakDbContext db, Guid categoryId, CancellationToken ct) =>
        db.Set<Category>().AsNoTracking().AnyAsync(category => category.Id == categoryId, ct);

    /// <summary>
    /// Сохраняет бизнес-изменение и запись аудита одним <c>SaveChanges</c>. Возвращает готовый
    /// 409 при нарушении уникальности или ссылочной целостности, иначе <c>null</c>.
    /// </summary>
    public static async Task<IResult?> SaveAsync(
        MayakDbContext db,
        string duplicationMessage,
        string referenceMessage,
        CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);

            return null;
        }
        catch (DbUpdateException exception) when (IsSqlState(exception, PostgresErrorCodes.UniqueViolation))
        {
            return ProblemResponses.Conflict(duplicationMessage);
        }
        catch (DbUpdateException exception) when (IsSqlState(exception, PostgresErrorCodes.ForeignKeyViolation))
        {
            return ProblemResponses.Conflict(referenceMessage);
        }
    }

    /// <summary>Снимок сущности для аудита (before/after) — сериализуется в jsonb.</summary>
    public static CategoryResponse ToResponse(Category category) =>
        new(category.Id, category.Code, category.Name, category.Icon, category.SortOrder, category.IsActive);

    private static bool IsSqlState(DbUpdateException exception, string sqlState) =>
        exception.InnerException is PostgresException { SqlState: var state }
        && string.Equals(state, sqlState, StringComparison.Ordinal);
}

/// <summary>Элемент ответа по схеме <c>Category</c>.</summary>
internal sealed record CategoryResponse(
    Guid Id,
    string Code,
    string Name,
    string? Icon,
    int SortOrder,
    bool IsActive);
