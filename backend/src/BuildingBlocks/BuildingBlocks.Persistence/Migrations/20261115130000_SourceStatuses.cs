using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Persistence.Migrations
{
    /// <summary>
    /// Статусы источника (db/schema.sql): статусы принадлежат
    /// источнику, у разных источников независимые списки (уникальность <c>(source_id, code)</c>).
    /// DDL взят дословно из эталона, включая палитру <c>color</c>, ссылку на библиотеку стилей
    /// <c>status_definitions(id)</c> (<c>on delete set null</c>).
    /// </summary>
    /// <remarks>
    /// Миграция написана вручную и не обновляет ModelSnapshot (как и остальные модульные
    /// миграции). Ограничение <c>unique (source_id, id)</c> нужно составному внешнему ключу
    /// <c>observations (source_id, source_status_id)</c> из миграции наблюдений.
    /// </remarks>
    [DbContext(typeof(MayakDbContext))]
    [Migration("20261115130000_SourceStatuses")]
    public partial class SourceStatuses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                create table source_statuses (
                  id                   uuid primary key default gen_random_uuid(),
                  source_id            uuid not null references sources(id) on delete cascade,
                  status_definition_id uuid references status_definitions(id) on delete set null,
                  code                 text not null,
                  name                 text not null,
                  description          text not null default '',    -- подсказка для LLM
                  color                text not null check (color in ('ok', 'limited', 'none', 'queue', 'schedule', 'stale')),
                  icon                 text not null,
                  sort_order           int not null default 0,
                  unique (source_id, code),
                  unique (source_id, id)                            -- для составного внешнего ключа из observations
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("drop table if exists source_statuses;");
        }
    }
}
