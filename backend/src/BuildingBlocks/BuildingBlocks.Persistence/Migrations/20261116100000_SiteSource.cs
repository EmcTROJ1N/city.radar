using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Persistence.Migrations
{
    /// <summary>
    /// Системный тип источника «Сайт» (<c>source_type = 'site'</c>) и ровно один такой
    /// источник на категорию. «Сайт» доступен фиксациям категории неявно, в
    /// <c>map_object_sources</c> не пишется, поэтому его <c>category_id</c> — обязательный
    /// атрибут источника, а не привязки.
    /// </summary>
    /// <remarks>
    /// Миграция создаёт «Сайт» для всех существующих категорий: копию справочника
    /// <c>status_definitions</c> (код, название, цвет, иконка, порядок по <c>severity</c>)
    /// и таймаут актуальности 3 часа. Дальше «Сайт» создаётся вместе с категорией через
    /// контракт <c>ISiteSourceProvisioner</c>. Миграция, как и остальные модульные,
    /// не обновляет ModelSnapshot.
    /// </remarks>
    [DbContext(typeof(MayakDbContext))]
    [Migration("20261116100000_SiteSource")]
    public partial class SiteSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                insert into source_types (code, name) values ('site', 'Сайт');

                alter table sources
                  add column category_id uuid references categories(id) on delete cascade;

                -- category_id задан только у «Сайта», и у «Сайта» он обязателен.
                alter table sources add constraint sources_site_category_check
                  check ((source_type = 'site') = (category_id is not null));

                -- Не больше одного «Сайта» на категорию.
                create unique index sources_site_category_uq
                  on sources (category_id) where source_type = 'site';

                -- «Сайт» для каждой существующей категории.
                insert into sources
                  (name, source_type, category_id, settings, user_prompt, link_by_text, is_enabled)
                select 'Сайт · ' || c.name, 'site', c.id, '{}', '', false, true
                from categories c;

                insert into source_policies (source_id, timeout, confidence_threshold)
                select s.id, interval '3 hours', null
                from sources s
                where s.source_type = 'site';

                -- Статусы «Сайта» — копия справочника его категории.
                insert into source_statuses
                  (source_id, status_definition_id, code, name, description, color, icon, sort_order)
                select s.id,
                       sd.id,
                       sd.code,
                       sd.name,
                       '',
                       sd.color,
                       sd.icon,
                       row_number() over (
                         partition by sd.category_id
                         order by sd.severity, sd.name, sd.id)::int
                from sources s
                join status_definitions sd on sd.category_id = s.category_id
                where s.source_type = 'site';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Политики и статусы удаляются каскадом вместе с источниками.
            migrationBuilder.Sql("delete from sources where source_type = 'site';");
            migrationBuilder.Sql("drop index if exists sources_site_category_uq;");
            migrationBuilder.Sql("alter table sources drop constraint if exists sources_site_category_check;");
            migrationBuilder.Sql("alter table sources drop column if exists category_id;");
            migrationBuilder.Sql("delete from source_types where code = 'site';");
        }
    }
}
