using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Persistence.Migrations
{
    /// <summary>
    /// Источники данных (db/schema.sql): расширяемый реестр типов
    /// <c>source_types</c> с seed-записями из <c>db/seed.sql</c>, источники <c>sources</c>
    /// и их политики <c>source_policies</c>. DDL взят дословно из эталона, включая check
    /// <c>state</c> и check политики (<c>timeout &gt; 0</c>, <c>confidence_threshold</c> в 0..1).
    /// </summary>
    /// <remarks>
    /// Миграция написана вручную и не обновляет ModelSnapshot (как и остальные модульные
    /// миграции). Колонки <c>sources.telegram_session_id</c> и <c>sources.proxy_id</c>
    /// ссылаются на таблицу <c>secrets</c>, которой ещё нет, поэтому здесь их нет: они будут
    /// добавлены миграцией <c>Secrets</c>. Таблица <c>source_statuses</c>
    ///. Внешний ключ <c>map_object_sources_source_id_fkey</c> добавляется здесь,
    /// потому что <c>map_object_sources</c> создана раньше без таблицы источников;
    /// <c>on delete cascade</c> — как в эталоне.
    /// </remarks>
    [DbContext(typeof(MayakDbContext))]
    [Migration("20261115120000_Sources")]
    public partial class Sources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                create table source_types (
                  code       text primary key,                     -- telegram_chat | max_chat | …
                  name       text not null,
                  is_enabled boolean not null default true
                );

                -- Типы источников из db/seed.sql: новые типы добавляются адаптером.
                insert into source_types (code, name) values
                  ('telegram_chat', 'Telegram-чат'), ('max_chat', 'MAX-чат');

                create table sources (
                  id                  uuid primary key default gen_random_uuid(),
                  name                text not null,
                  source_type         text not null references source_types(code),
                  settings            jsonb not null default '{}',   -- идентификатор чата и параметры типа
                  user_prompt         text not null default '',      -- промпт-фильтр пользователя
                  link_by_text        boolean not null default true, -- режим «определять фиксацию по тексту»
                  is_enabled          boolean not null default true,
                  state               text not null default 'unknown'
                                        check (state in ('unknown', 'ok', 'silent', 'flood_wait', 'auth_error', 'error')),
                  last_message_at     timestamptz,
                  -- TODO: telegram_session_id uuid references secrets(id),
                  --                 proxy_id uuid references secrets(id) — добавить миграцией Secrets.
                  created_by          uuid references users(id),
                  created_at          timestamptz not null default now(),
                  updated_at          timestamptz not null default now()
                );
                create trigger sources_updated before update on sources
                  for each row execute function set_updated_at();

                create table source_policies (
                  source_id            uuid primary key references sources(id) on delete cascade,
                  timeout              interval not null check (timeout > interval '0'),  -- таймаут актуальности
                  confidence_threshold real check (confidence_threshold between 0 and 1), -- null = значение по умолчанию из настроек
                  prefilter            jsonb
                );

                alter table map_object_sources
                  add constraint map_object_sources_source_id_fkey
                  foreign key (source_id) references sources(id) on delete cascade;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Сначала снимаем FK, иначе sources не удалить из-за зависимых привязок.
            migrationBuilder.Sql(
                "alter table map_object_sources drop constraint if exists map_object_sources_source_id_fkey;");
            migrationBuilder.Sql("drop table if exists source_policies;");
            migrationBuilder.Sql("drop table if exists sources;");
            migrationBuilder.Sql("drop table if exists source_types;");
        }
    }
}
