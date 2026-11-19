-- Схема БД портала «Городские ресурсы ДНР».
-- PostgreSQL 16 + PostGIS. Идентификаторы — uuid; время — timestamptz (UTC).
-- Этап 2 (расписания, подписки) сюда не входит.
-- Это эталонное описание модели: в проекте таблицы создаются EF Core-миграциями.

create extension if not exists postgis;
create extension if not exists pg_trgm;

-- Общая функция: автообновление updated_at
create function set_updated_at() returns trigger language plpgsql as $$
begin
  new.updated_at = now();
  return new;
end $$;

-- =====================================================================
-- 1. Идентификация и доступ (RBAC на правах)
-- =====================================================================

-- Таблицы ASP.NET Core Identity: ключи Guid, имена snake_case (настраивается в EF Core).
-- Вход только через MAX, пароли и e-mail не используются, поэтому их колонок нет.
-- Права (permissions) — claims роли: claim_type = 'permission', claim_value = 'source.manage'.
-- Отдельного справочника прав в БД нет: коды прав — константы в коде.
-- Аноним — не роль в БД: map.read выдаётся всем без входа на уровне приложения.
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

-- =====================================================================
-- 2. Аудит (append-only) и глобальные настройки
-- =====================================================================

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

create function forbid_change() returns trigger language plpgsql as $$
begin
  raise exception 'Таблица % только для добавления записей', tg_table_name;
end $$;

create trigger audit_entries_append_only
  before update or delete on audit_entries
  for each row execute function forbid_change();

create table global_settings (
  key        text primary key,
  value      jsonb not null,
  updated_by uuid references users(id),
  updated_at timestamptz not null default now()
);

-- =====================================================================
-- 3. Каталог
-- =====================================================================

create table categories (
  id         uuid primary key default gen_random_uuid(),
  code       text not null unique,
  name       text not null,
  icon       text,
  sort_order int not null default 0,
  is_active  boolean not null default true
);

create table object_types (
  id            uuid primary key default gen_random_uuid(),
  category_id   uuid not null references categories(id),
  code          text not null unique,
  name          text not null,
  geometry_kind text not null default 'any' check (geometry_kind in ('point', 'polygon', 'any'))
);

create table object_groups (
  id                    uuid primary key default gen_random_uuid(),
  category_id           uuid references categories(id),
  name                  text not null,
  color                 text,
  is_visible_by_default boolean not null default true
);
create unique index object_groups_name_uq on object_groups (lower(name));

-- Фиксация на карте: точка или геозона. Группа и тип необязательны.
create table map_objects (
  id             uuid primary key default gen_random_uuid(),
  object_type_id uuid references object_types(id),
  group_id       uuid references object_groups(id) on delete set null,
  name           text not null,
  coverage_kind  text not null check (coverage_kind in ('point', 'polygon')),
  geometry       geography(Geometry, 4326) not null,
  address        text,
  is_active      boolean not null default true,
  created_by     uuid references users(id),
  created_at     timestamptz not null default now(),
  updated_at     timestamptz not null default now(),
  check (
    (coverage_kind = 'point'   and geometrytype(geometry::geometry) = 'POINT') or
    (coverage_kind = 'polygon' and geometrytype(geometry::geometry) = 'POLYGON')
  )
);
create index map_objects_geometry_gist on map_objects using gist (geometry);
create index map_objects_name_trgm on map_objects using gin (name gin_trgm_ops);
create index map_objects_group_idx on map_objects (group_id);
create trigger map_objects_updated before update on map_objects
  for each row execute function set_updated_at();

-- Библиотека статусов (стили). Цвет — ключ палитры оформления, а не произвольный hex (контраст в обеих темах). Статусы, которые «докладывает» источник, лежат в source_statuses.
create table status_definitions (
  id          uuid primary key default gen_random_uuid(),
  category_id uuid references categories(id),
  code        text not null unique,
  name        text not null,
  color       text not null check (color in ('ok', 'limited', 'none', 'queue', 'schedule', 'stale')),
  icon        text not null,
  severity    int not null default 0,
  is_positive boolean not null default false
);

-- =====================================================================
-- 4. Источники данных
-- =====================================================================

create table source_types (
  code       text primary key,                     -- telegram_chat | max_chat | …
  name       text not null,
  is_enabled boolean not null default true
);

-- Зашифрованные секреты (ключ шифрования хранится вне БД; значения наружу не отдаются).
-- Прокси: глобальный — ключ proxy_secret_id в global_settings; на аккаунт и LLM — secrets.proxy_id; на источник — sources.proxy_id.
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

create table sources (
  id                  uuid primary key default gen_random_uuid(),
  name                text not null,
  source_type         text not null references source_types(code),
  category_id         uuid references categories(id) on delete cascade, -- только у системного «Сайта»
  settings            jsonb not null default '{}',   -- идентификатор чата и параметры типа
  user_prompt         text not null default '',      -- промпт-фильтр пользователя
  link_by_text        boolean not null default true, -- режим «определять фиксацию по тексту»
  is_enabled          boolean not null default true,
  state               text not null default 'unknown'
                        check (state in ('unknown', 'ok', 'silent', 'flood_wait', 'auth_error', 'error')),
  last_message_at     timestamptz,
  telegram_session_id uuid references secrets(id),
  proxy_id            uuid references secrets(id),
  created_by          uuid references users(id),
  created_at          timestamptz not null default now(),
  updated_at          timestamptz not null default now(),
  -- category_id задан ровно у «Сайта»: один системный источник на категорию.
  check ((source_type = 'site') = (category_id is not null))
);
create unique index sources_site_category_uq on sources (category_id) where source_type = 'site';
create trigger sources_updated before update on sources
  for each row execute function set_updated_at();

create table source_policies (
  source_id            uuid primary key references sources(id) on delete cascade,
  timeout              interval not null check (timeout > interval '0'),  -- таймаут актуальности
  confidence_threshold real check (confidence_threshold between 0 and 1), -- null = значение по умолчанию из настроек
  prefilter            jsonb
);

-- Статусы принадлежат источнику
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

-- Привязка источников к фиксациям (у фиксации несколько источников, источник — к нескольким фиксациям)
create table map_object_sources (
  map_object_id uuid not null references map_objects(id) on delete cascade,
  source_id     uuid not null references sources(id) on delete cascade,
  primary key (map_object_id, source_id)
);
create index map_object_sources_source_idx on map_object_sources (source_id);

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

-- =====================================================================
-- 5. Ядро: наблюдения и текущий статус
-- =====================================================================

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
  address          text,                            -- адрес из сообщения чата («улица дом»), для точек на карте
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

-- =====================================================================
-- 6. Модерация
-- =====================================================================

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
  check ((state = 'open') = (resolved_at is null)),
  check ((kind = 'complaint') = (reporter_id is not null))
);
create index moderation_cases_open_idx on moderation_cases (created_at) where state = 'open';

-- =====================================================================
-- 7. Outbox: обмен Api <-> Worker (transactional outbox)
-- =====================================================================

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

