using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Persistence.Migrations
{
    /// <summary>
    /// Ядро наблюдений: исходные сообщения
    /// (<c>raw_messages</c>), неизменяемые наблюдения (<c>observations</c>) и проекция итогового
    /// статуса для карты (<c>current_statuses</c>). DDL взят дословно из <c>db/schema.sql</c>,
    /// включая составной внешний ключ <c>(source_id, source_status_id)</c> на
    /// <c>source_statuses (source_id, id)</c>, check-констрейнты, частичные индексы
    /// <c>observations_active_idx</c>/<c>observations_reporter_idx</c>, функцию
    /// <c>observations_guard()</c> и триггер <c>observations_immutable</c>.
    /// </summary>
    /// <remarks>
    /// Миграция написана вручную и не обновляет ModelSnapshot (как и остальные модульные
    /// миграции). Уникальность <c>source_statuses (source_id, id)</c> уже создана миграцией
    /// <c>SourceStatuses</c> (<c>source_statuses_source_id_id_key</c>) — дополнительный
    /// <c>ALTER TABLE ... ADD CONSTRAINT</c> не нужен.
    /// </remarks>
    [DbContext(typeof(MayakDbContext))]
    [Migration("20261115150000_Observations")]
    public partial class Observations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                create table raw_messages (
                  id          uuid primary key default gen_random_uuid(),
                  source_id   uuid not null references sources(id) on delete cascade,
                  external_id text not null,                        -- идентификатор сообщения во внешней системе
                  text        text not null,
                  author      text,                                 -- хранится, но через публичный API не отдаётся
                  chat        text,
                  link        text,
                  posted_at   timestamptz not null,
                  received_at timestamptz not null default now(),
                  unique (source_id, external_id)
                );
                create index raw_messages_source_time_idx on raw_messages (source_id, posted_at desc);

                create table observations (
                  id               uuid primary key default gen_random_uuid(),
                  map_object_id    uuid not null references map_objects(id),   -- фиксации с историей не удаляются, только is_active = false
                  source_id        uuid not null references sources(id),
                  source_status_id uuid not null,
                  source_type      text not null check (source_type in ('manual', 'moderator', 'telegram', 'max')),
                  reporter_id      uuid references users(id),       -- для manual и moderator
                  observed_at      timestamptz not null,
                  received_at      timestamptz not null default now(),
                  expires_at       timestamptz not null,            -- observed_at + таймаут источника
                  confidence       real check (confidence between 0 and 1),
                  raw_message_id   uuid references raw_messages(id),
                  comment          text,                            -- комментарий пользователя
                  geo_distance_m int check (geo_distance_m >= 0), -- расстояние от геолокации пользователя до фиксации; только информационный признак
                  is_retracted     boolean not null default false,
                  retracted_by     uuid references users(id),
                  retracted_at     timestamptz,
                  retract_reason   text,
                  -- статус обязан принадлежать выбранному источнику
                  foreign key (source_id, source_status_id) references source_statuses (source_id, id),
                  check (expires_at > observed_at),
                  check ((source_type in ('manual', 'moderator')) = (reporter_id is not null)),
                  check ((source_type in ('telegram', 'max')) = (raw_message_id is not null)),
                  check (is_retracted = (retracted_at is not null))
                );
                create index observations_active_idx on observations (map_object_id, expires_at) where not is_retracted;
                create index observations_object_time_idx on observations (map_object_id, observed_at desc);
                create index observations_source_time_idx on observations (source_id, observed_at desc);
                create index observations_reporter_idx on observations (reporter_id, observed_at desc) where reporter_id is not null;

                -- Наблюдение неизменяемо; допустим только отзыв
                create function observations_guard() returns trigger language plpgsql as $$
                begin
                  if tg_op = 'DELETE' then
                    raise exception 'Наблюдения нельзя удалять, только отзывать';
                  end if;
                  if (to_jsonb(new) - 'is_retracted' - 'retracted_by' - 'retracted_at' - 'retract_reason')
                     is distinct from
                     (to_jsonb(old) - 'is_retracted' - 'retracted_by' - 'retracted_at' - 'retract_reason') then
                    raise exception 'Наблюдение неизменяемо; допустим только отзыв';
                  end if;
                  if old.is_retracted then
                    raise exception 'Отозванное наблюдение изменять нельзя';
                  end if;
                  return new;
                end $$;
                create trigger observations_immutable
                  before update or delete on observations
                  for each row execute function observations_guard();

                -- Проекция для карты: одна строка на фиксацию
                create table current_statuses (
                  map_object_id      uuid primary key references map_objects(id) on delete cascade,
                  source_status_id   uuid references source_statuses(id),  -- null = «неизвестно»
                  source_id          uuid references sources(id),
                  observation_id     uuid references observations(id),
                  observed_at        timestamptz,
                  freshness          text not null check (freshness in ('fresh', 'aging', 'stale')),
                  source_type        text,
                  observation_count  int not null default 0,
                  updated_at         timestamptz not null default now()
                );
                create index current_statuses_updated_idx on current_statuses (updated_at);   -- для ETag
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                drop table if exists current_statuses;
                drop table if exists observations;
                drop function if exists observations_guard();
                drop table if exists raw_messages;
                """);
        }
    }
}
