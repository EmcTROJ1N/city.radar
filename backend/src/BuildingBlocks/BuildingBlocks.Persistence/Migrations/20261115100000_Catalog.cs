using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Persistence.Migrations
{
    /// <summary>
    /// Каталог (db/schema.sql): категории, типы объектов с JSON Schema атрибутов,
    /// группы и библиотека статусов. DDL взят дословно из эталона, включая уникальный индекс
    /// <c>object_groups_name_uq</c> по <c>lower(name)</c> и check палитры цветов.
    /// </summary>
    /// <remarks>
    /// Миграция написана вручную и не обновляет ModelSnapshot: модель модуля собирается
    /// <c>CatalogModelContributor</c>, а расхождение с историей миграций подавлено через
    /// <c>RelationalEventId.PendingModelChangesWarning</c>. Таблицы <c>map_objects</c> и
    /// <c>source_statuses</c>, которые ссылаются на эти таблицы, создаются своими миграциями.
    /// </remarks>
    [DbContext(typeof(MayakDbContext))]
    [Migration("20261115100000_Catalog")]
    public partial class Catalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                create table categories (
                  id         uuid primary key default gen_random_uuid(),
                  code       text not null unique,
                  name       text not null,
                  icon       text,
                  sort_order int not null default 0,
                  is_active  boolean not null default true
                );

                create table object_types (
                  id                uuid primary key default gen_random_uuid(),
                  category_id       uuid not null references categories(id),
                  code              text not null unique,
                  name              text not null,
                  attributes_schema jsonb not null default '{}',   -- JSON Schema атрибутов
                  geometry_kind     text not null default 'any' check (geometry_kind in ('point', 'polygon', 'any'))
                );

                create table object_groups (
                  id                    uuid primary key default gen_random_uuid(),
                  category_id           uuid references categories(id),
                  name                  text not null,
                  color                 text,
                  is_visible_by_default boolean not null default true
                );
                create unique index object_groups_name_uq on object_groups (lower(name));

                -- Библиотека статусов (стили). Цвет — ключ палитры оформления, а не произвольный hex (контраст в обеих темах). Статусы, которые «докладывает» источник, лежат в source_statuses.
                create table status_definitions (
                  id          uuid primary key default gen_random_uuid(),
                  category_id uuid references categories(id),
                  code        text not null unique,
                  name        text not null,
                  color       text not null check (color in ('ok', 'limited', 'none', 'queue', 'schedule', 'stale')),
                  icon        text not null,
                  severity    int not null default 0,
                  is_positive boolean not null default false
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Порядок обратен созданию из-за FK на categories(id).
            migrationBuilder.Sql("drop table if exists status_definitions;");
            migrationBuilder.Sql("drop table if exists object_groups;");
            migrationBuilder.Sql("drop table if exists object_types;");
            migrationBuilder.Sql("drop table if exists categories;");
        }
    }
}
