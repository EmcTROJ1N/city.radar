using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.Persistence;

/// <summary>
/// Модуль отдаёт EF Core-модель своих сущностей. Конфигурации остаются внутренними для модуля,
/// а единый <see cref="MayakDbContext"/> собирает из них одну модель и одну историю миграций.
/// </summary>
public interface IModelContributor
{
    void Configure(ModelBuilder modelBuilder);
}
