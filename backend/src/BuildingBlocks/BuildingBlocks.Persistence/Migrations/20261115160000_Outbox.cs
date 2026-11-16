using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Persistence.Migrations
{
    /// <summary>
    /// Transactional outbox (решение): таблица <c>outbox_messages</c> и
    /// частичный индекс <c>outbox_pending_idx</c> — DDL дословно из <c>db/schema.sql</c>
    ///. Через таблицу обмениваются Api и IngestionWorker: сообщение сохраняется
    /// в одной транзакции с бизнес-данными, а получатель разбирает его по своему destination.
    /// </summary>
    /// <remarks>
    /// Миграция написана вручную и не обновляет ModelSnapshot (как и остальные модульные
    /// миграции). Check <c>destination in ('api', 'worker')</c> ограничивает стороны обмена:
    /// в одном процессе диспетчер обслуживает ровно один destination.
    /// </remarks>
    [DbContext(typeof(MayakDbContext))]
    [Migration("20261115160000_Outbox")]
    public partial class Outbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                create table outbox_messages (
                  id            uuid primary key default gen_random_uuid(),
                  destination   text not null check (destination in ('api', 'worker')),
                  type          text not null,                      -- ObservationProposed, ModerationCaseRequested, …
                  payload       jsonb not null,
                  created_at    timestamptz not null default now(),
                  available_at  timestamptz not null default now(),
                  processed_at  timestamptz,
                  attempts      int not null default 0,
                  last_error    text
                );
                create index outbox_pending_idx on outbox_messages (destination, available_at) where processed_at is null;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop index if exists outbox_pending_idx;
                drop table if exists outbox_messages;
                """);
        }
    }
}
