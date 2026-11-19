using BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Persistence.Migrations
{
    /// <summary>
    /// Адрес из сообщения чата у наблюдения: LLM извлекает «улица дом», по нему карта
    /// строит временные точки внутри выбранной геозоны.
    /// </summary>
    /// <remarks>
    /// Колонка nullable и без default: <c>add column</c> не переписывает строки, поэтому триггер
    /// неизменяемости <c>observations_guard</c> не срабатывает. Старые наблюдения остаются без адреса —
    /// дозаполнить их нельзя. Миграция, как и остальные модульные, не обновляет ModelSnapshot.
    /// </remarks>
    [DbContext(typeof(MayakDbContext))]
    [Migration("20261118120000_ObservationAddress")]
    public partial class ObservationAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("alter table observations add column if not exists address text;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("alter table observations drop column if exists address;");
        }
    }
}
