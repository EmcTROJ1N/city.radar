using BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Persistence.Migrations
{
    /// <summary>
    /// Очередь модерации: таблица
    /// <c>moderation_cases</c> и частичный индекс <c>moderation_cases_open_idx</c>. DDL взят
    /// дословно из <c>db/schema.sql</c>, включая check-ограничения
    /// <c>(state = 'open') = (resolved_at is null)</c> и
    /// <c>(kind = 'complaint') = (reporter_id is not null)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Миграция написана вручную и не обновляет ModelSnapshot. Одно осознанное отличие от
    /// эталона: колонка <c>outbox_message_id uuid unique</c> — ключ идемпотентности обработчика
    /// <c>ModerationCaseRequested</c>: повторная доставка сообщения не
    /// создаёт второй кейс. В <c>db/schema.sql</c> такой колонки нет.
    /// </para>
    /// </remarks>
    [DbContext(typeof(MayakDbContext))]
    [Migration("20261115170000_Moderation")]
    public partial class Moderation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                create table moderation_cases (
                  id             uuid primary key default gen_random_uuid(),
                  kind           text not null check (kind in
                                   ('low_confidence', 'invalid_status', 'object_not_found', 'invalid_response', 'anomaly', 'complaint')),
                  state          text not null default 'open' check (state in ('open', 'approved', 'rejected', 'dismissed')),
                  source_id      uuid references sources(id) on delete set null,
                  raw_message_id uuid references raw_messages(id) on delete set null,
                  observation_id uuid references observations(id),
                  map_object_id  uuid references map_objects(id) on delete set null,
                  reporter_id    uuid references users(id),         -- автор жалобы (kind = complaint)
                  comment        text,                              -- текст жалобы
                  proposed       jsonb,                             -- предложенный статус, confidence, evidence_quote
                  resolution     jsonb,
                  created_at     timestamptz not null default now(),
                  resolved_by    uuid references users(id),
                  resolved_at    timestamptz,
                  -- Добавка к эталону: ключ идемпотентности обработчика ModerationCaseRequested.
                  outbox_message_id uuid unique,
                  check ((state = 'open') = (resolved_at is null)),
                  check ((kind = 'complaint') = (reporter_id is not null))
                );
                create index moderation_cases_open_idx on moderation_cases (created_at) where state = 'open';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("drop table if exists moderation_cases;");
        }
    }
}
