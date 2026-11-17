using BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Persistence.Migrations
{
    /// <summary>
    /// Удаляет JSON-атрибуты фиксаций и их схему у типов объектов.
    /// </summary>
    /// <remarks>
    /// Атрибуты не использовались ни в статусах, ни в фильтрах, ни в карточке, поэтому
    /// убираются из модели целиком: <c>map_objects.attributes</c> вместе с GIN-индексом
    /// и <c>object_types.attributes_schema</c>. Миграция, как и остальные модульные,
    /// не обновляет ModelSnapshot. Данные атрибутов не переносятся — <c>Down</c>
    /// восстанавливает только пустые колонки.
    /// </remarks>
    [DbContext(typeof(MayakDbContext))]
    [Migration("20261117120000_DropAttributes")]
    public partial class DropAttributes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop index if exists map_objects_attributes_gin;
                alter table map_objects drop column if exists attributes;
                alter table object_types drop column if exists attributes_schema;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                alter table object_types
                  add column if not exists attributes_schema jsonb not null default '{}';

                alter table map_objects
                  add column if not exists attributes jsonb not null default '{}';

                create index if not exists map_objects_attributes_gin
                  on map_objects using gin (attributes);
                """);
        }
    }
}
