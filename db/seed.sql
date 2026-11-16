-- Начальные данные для любого окружения: роли, права, типы источников, настройки по умолчанию.

insert into roles (name, normalized_name) values
  ('user', 'USER'), ('moderator', 'MODERATOR'), ('admin', 'ADMIN');

-- Права роли — claims типа permission. Аноним — не роль: map.read выдаётся всем на уровне приложения.
insert into role_claims (role_id, claim_value)
select r.id, c.permission
from roles r
join (values
  ('user', 'map.read'),
  ('user', 'observation.create'),
  ('moderator', 'map.read'),
  ('moderator', 'observation.create'),
  ('moderator', 'observation.retract'),
  ('moderator', 'catalog.category.manage'),
  ('moderator', 'catalog.objecttype.manage'),
  ('moderator', 'catalog.object.manage'),
  ('moderator', 'catalog.group.manage'),
  ('moderator', 'catalog.status.manage'),
  ('moderator', 'source.manage'),
  ('moderator', 'moderation.queue.process'),
  ('admin', 'map.read'),
  ('admin', 'observation.create'),
  ('admin', 'observation.retract'),
  ('admin', 'catalog.category.manage'),
  ('admin', 'catalog.objecttype.manage'),
  ('admin', 'catalog.object.manage'),
  ('admin', 'catalog.group.manage'),
  ('admin', 'catalog.status.manage'),
  ('admin', 'source.manage'),
  ('admin', 'moderation.queue.process'),
  ('admin', 'source.secrets.manage'),
  ('admin', 'settings.global.manage'),
  ('admin', 'users.roles.assign'),
  ('admin', 'audit.read')
) as c(role_name, permission) on c.role_name = r.name;

insert into source_types (code, name) values
  ('telegram_chat', 'Telegram-чат'), ('max_chat', 'MAX-чат');

insert into global_settings (key, value) values
  ('observation_rate_per_hour', '20'),
  ('comment_max_length', '500'),
  ('confidence_threshold_default', '0.7'),
  ('anomaly_window_minutes', '5'),
  ('anomaly_threshold', '30'),
  ('freshness_fresh_ratio', '0.5'); -- граница Fresh/Aging: доля таймаута
