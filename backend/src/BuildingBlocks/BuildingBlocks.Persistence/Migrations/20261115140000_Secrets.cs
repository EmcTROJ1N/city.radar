using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Persistence.Migrations
{
    /// <summary>
    /// Зашифрованные секреты (db/schema.sql):
    /// Telegram-сессии, прокси и единственный секрет LLM. DDL взят дословно из эталона, включая
    /// частичный уникальный индекс <c>secrets_single_llm</c>, триггер <c>secrets_updated</c> и
    /// check, запрещающий <c>proxy_id</c> у самого прокси. Колонки <c>sources.telegram_session_id</c>
    /// и <c>sources.proxy_id</c> добавляются этой же миграцией — в эталоне они объявлены в таблице
    /// <c>sources</c>.
    /// </summary>
    /// <remarks>
    /// Миграция написана вручную и не обновляет ModelSnapshot (как и остальные модульные
    /// миграции). Значения секретов в БД лежат только в <c>ciphertext</c>; ключ шифрования —
    /// вне БД.
    /// </remarks>
    [DbContext(typeof(MayakDbContext))]
    [Migration("20261115140000_Secrets")]
    public partial class Secrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                create table secrets (
                  id          uuid primary key default gen_random_uuid(),
                  kind        text not null check (kind in ('telegram_session', 'proxy', 'llm')),
                  name        text not null,
                  ciphertext  bytea not null,
                  key_version int not null default 1,
                  proxy_id uuid references secrets(id), -- прокси для аккаунта Telegram или LLM
                  created_by  uuid references users(id),
                  created_at  timestamptz not null default now(),
                  updated_at  timestamptz not null default now(),
                  unique (kind, name),
                  check (proxy_id is null or kind <> 'proxy')
                );
                create unique index secrets_single_llm on secrets (kind) where kind = 'llm';
                create trigger secrets_updated before update on secrets
                  for each row execute function set_updated_at();

                alter table sources add column telegram_session_id uuid references secrets(id);
                alter table sources add column proxy_id uuid references secrets(id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Сначала снимаем колонки-ссылки, иначе таблицу secrets не удалить из-за FK.
            migrationBuilder.Sql("alter table sources drop column if exists proxy_id;");
            migrationBuilder.Sql("alter table sources drop column if exists telegram_session_id;");
            migrationBuilder.Sql("drop table if exists secrets;");
        }
    }
}
