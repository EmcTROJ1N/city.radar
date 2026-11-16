using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Persistence.Migrations
{
    /// <summary>
    /// Таблицы Identity &amp; Access: пользователи, роли, права ролей, refresh-токены,
    /// append-only журнал аудита и глобальные настройки (db/schema.sql).
    /// </summary>
    /// <remarks>
    /// Миграция написана вручную: DDL взят дословно из <c>db/schema.sql</c>, включая триггер
    /// <c>audit_entries_append_only</c>, который EF Core не умеет выражать. Функция
    /// <c>forbid_change()</c> создаётся в <see cref="Baseline"/> и здесь не дублируется.
    /// ModelSnapshot не обновляется намеренно: модель модуля собирается
    /// <c>IdentityModelContributor</c>, а расхождение с историей миграций подавлено
    /// через <c>RelationalEventId.PendingModelChangesWarning</c>.
    /// </remarks>
    [DbContext(typeof(MayakDbContext))]
    [Migration("20261115090000_Identity")]
    public partial class Identity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                create table users (
                  id                   uuid primary key default gen_random_uuid(),
                  user_name            text not null,                -- Identity: = max_user_id в текстовом виде
                  normalized_user_name text not null unique,
                  security_stamp       text,
                  concurrency_stamp    text,
                  max_user_id          bigint not null unique,       -- идентификатор в мессенджере MAX
                  display_name         text not null,
                  created_at           timestamptz not null default now(),
                  last_login_at        timestamptz
                );

                create table roles (
                  id                uuid primary key default gen_random_uuid(),
                  name              text not null,                   -- user | moderator | admin
                  normalized_name   text not null unique,
                  concurrency_stamp text
                );

                create table user_roles (
                  user_id uuid not null references users(id) on delete cascade,
                  role_id uuid not null references roles(id) on delete cascade,
                  primary key (user_id, role_id)
                );

                create table role_claims (
                  id          int generated always as identity primary key,
                  role_id     uuid not null references roles(id) on delete cascade,
                  claim_type  text not null default 'permission',
                  claim_value text not null,
                  unique (role_id, claim_type, claim_value)
                );

                create table refresh_tokens (
                  id         uuid primary key default gen_random_uuid(),
                  user_id    uuid not null references users(id) on delete cascade,
                  token_hash bytea not null unique,                -- хранится только хеш
                  created_at timestamptz not null default now(),
                  expires_at timestamptz not null,
                  revoked_at timestamptz
                );
                create index refresh_tokens_user_idx on refresh_tokens (user_id);

                create table audit_entries (
                  id          bigint generated always as identity primary key,
                  actor_id    uuid references users(id),
                  action      text not null,
                  entity_type text,
                  entity_id   text,
                  before      jsonb,
                  after       jsonb,
                  reason      text,
                  ip          inet,
                  user_agent  text,
                  at          timestamptz not null default now()
                );
                create index audit_at_idx on audit_entries (at desc);
                create index audit_actor_idx on audit_entries (actor_id, at desc);
                create index audit_entity_idx on audit_entries (entity_type, entity_id);

                create trigger audit_entries_append_only
                  before update or delete on audit_entries
                  for each row execute function forbid_change();

                create table global_settings (
                  key        text primary key,
                  value      jsonb not null,
                  updated_by uuid references users(id),
                  updated_at timestamptz not null default now()
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Триггер и индексы удаляются вместе с таблицами. Порядок обратен созданию из-за FK.
            migrationBuilder.Sql("drop table if exists global_settings;");
            migrationBuilder.Sql("drop table if exists audit_entries;");
            migrationBuilder.Sql("drop table if exists refresh_tokens;");
            migrationBuilder.Sql("drop table if exists role_claims;");
            migrationBuilder.Sql("drop table if exists user_roles;");
            migrationBuilder.Sql("drop table if exists roles;");
            migrationBuilder.Sql("drop table if exists users;");
        }
    }
}
