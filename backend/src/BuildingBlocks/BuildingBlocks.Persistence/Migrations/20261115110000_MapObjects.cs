using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Persistence.Migrations
{
    /// <summary>
    /// Фиксации на карте (db/schema.sql): точки и геозоны с геометрией
    /// <c>geography(Geometry, 4326)</c>, привязки источников, GiST/GIN/trgm-индексы и триггер
    /// <c>map_objects_updated</c>. DDL взят дословно из эталона, включая check соответствия
    /// <c>coverage_kind</c> типу геометрии.
    /// </summary>
    /// <remarks>
    /// Миграция написана вручную и не обновляет ModelSnapshot (как и остальные модульные
    /// миграции). Таблицы <c>sources</c> на этом этапе ещё нет, поэтому:
    /// <list type="bullet">
    ///   <item>
    ///     <c>map_object_sources</c> создаётся без FK на <c>sources(id)</c>: ограничение
    /// <c>map_object_sources_source_id_fkey</c> добавит миграция <c>Sources</c>.
    ///   </item>
    ///   <item>
    ///     Внешние ключи других будущих таблиц (наблюдения, статусы) на <c>map_objects</c>
    ///     здесь не создаются — они появятся вместе со своими таблицами.
    ///   </item>
    /// </list>
    /// Функция <c>set_updated_at()</c> создаётся в <see cref="Baseline"/>.
    /// </remarks>
    [DbContext(typeof(MayakDbContext))]
    [Migration("20261115110000_MapObjects")]
    public partial class MapObjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                -- Фиксация на карте: точка или геозона. Группа и тип необязательны.
                create table map_objects (
                  id             uuid primary key default gen_random_uuid(),
                  object_type_id uuid references object_types(id),
                  group_id       uuid references object_groups(id) on delete set null,
                  name           text not null,
                  coverage_kind  text not null check (coverage_kind in ('point', 'polygon')),
                  geometry       geography(Geometry, 4326) not null,
                  attributes     jsonb not null default '{}',
                  address        text,
                  is_active      boolean not null default true,
                  created_by     uuid references users(id),
                  created_at     timestamptz not null default now(),
                  updated_at     timestamptz not null default now(),
                  check (
                    (coverage_kind = 'point'   and geometrytype(geometry::geometry) = 'POINT') or
                    (coverage_kind = 'polygon' and geometrytype(geometry::geometry) = 'POLYGON')
                  )
                );
                create index map_objects_geometry_gist on map_objects using gist (geometry);
                create index map_objects_attributes_gin on map_objects using gin (attributes);
                create index map_objects_name_trgm on map_objects using gin (name gin_trgm_ops);
                create index map_objects_group_idx on map_objects (group_id);
                create trigger map_objects_updated before update on map_objects
                  for each row execute function set_updated_at();

                -- Привязка источников к фиксациям (у фиксации несколько источников, источник — к нескольким фиксациям).
                -- TODO: источников ещё нет; FK map_object_sources_source_id_fkey на sources(id)
                -- добавляется миграцией Sources.
                create table map_object_sources (
                  map_object_id uuid not null references map_objects(id) on delete cascade,
                  source_id     uuid not null,
                  primary key (map_object_id, source_id)
                );
                create index map_object_sources_source_idx on map_object_sources (source_id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("drop table if exists map_object_sources;");
            migrationBuilder.Sql("drop table if exists map_objects;");
        }
    }
}
