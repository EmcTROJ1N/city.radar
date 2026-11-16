using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Persistence.Migrations
{
    /// <summary>
    /// Деталь health-check источника: срок окончания FloodWait или
    /// текст ошибки авторизации/адаптера для UI. В эталоне <c>db/schema.sql</c> такой колонки
    /// нет — в контракте <c>SourceHealth</c> для неё есть поля <c>floodWaitUntil</c> и
    /// <c>lastError</c>, поэтому добавляется только недостающая колонка <c>state_detail</c>;
    /// <c>state</c> и <c>last_message_at</c> уже созданы миграцией <c>Sources</c>.
    /// </summary>
    /// <remarks>
    /// Миграция написана вручную и не обновляет ModelSnapshot (как и остальные модульные
    /// миграции). Health — служебные данные: при записи состояния аудит не создаётся.
    /// </remarks>
    [DbContext(typeof(MayakDbContext))]
    [Migration("20261115145000_SourceHealth")]
    public partial class SourceHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("alter table sources add column state_detail text;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("alter table sources drop column if exists state_detail;");
        }
    }
}
