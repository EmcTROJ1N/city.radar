-- Данные для разработки и тестирования. В продуктивном окружении не применять.

insert into users (id, user_name, normalized_user_name, max_user_id, display_name) values
  ('00000000-0000-0000-0000-0000000000a1', '900000001', '900000001', 900000001, 'Виктор'),
  ('00000000-0000-0000-0000-0000000000a2', '900000002', '900000002', 900000002, 'Марина'),
  ('00000000-0000-0000-0000-0000000000a3', '900000003', '900000003', 900000003, 'Ольга'),
  ('00000000-0000-0000-0000-0000000000a4', '900000004', '900000004', 900000004, 'Игорь'),
  ('00000000-0000-0000-0000-0000000000a5', '900000005', '900000005', 900000005, 'Герман');

insert into user_roles (user_id, role_id)
select v.user_id::uuid, r.id
from (values
  ('00000000-0000-0000-0000-0000000000a1', 'user'),
  ('00000000-0000-0000-0000-0000000000a2', 'user'),
  ('00000000-0000-0000-0000-0000000000a3', 'moderator'),
  ('00000000-0000-0000-0000-0000000000a4', 'moderator'),
  ('00000000-0000-0000-0000-0000000000a5', 'admin')
) as v(user_id, role_name)
join roles r on r.name = v.role_name;

-- Базовый каталог воды
insert into categories (id, code, name, icon, sort_order) values
  ('00000000-0000-0000-0000-0000000000c1', 'water', 'Вода', 'drop', 1);
insert into object_types (id, category_id, code, name, geometry_kind) values
  ('00000000-0000-0000-0000-0000000000b1', '00000000-0000-0000-0000-0000000000c1', 'district', 'Район', 'polygon');
insert into status_definitions (category_id, code, name, color, icon, severity, is_positive) values
  ('00000000-0000-0000-0000-0000000000c1', 'ok', 'Вода есть', 'ok', 'drop', 0, true),
  ('00000000-0000-0000-0000-0000000000c1', 'none', 'Воды нет', 'none', 'cross', 3, false);

-- Источник «Вода» со статусами, политикой и одной фиксацией
insert into sources (id, name, source_type, settings, user_prompt) values
  ('00000000-0000-0000-0000-0000000000e1', 'Чат жильцов Калининского', 'telegram_chat',
   '{"chatId": "@kalininsky_water", "mode": "bot"}',
   'Ищи в чате все упоминания про подачу и прекращение подачи воды.');
insert into source_policies (source_id, timeout) values
  ('00000000-0000-0000-0000-0000000000e1', interval '6 hours');
insert into source_statuses (source_id, code, name, description, color, icon, sort_order) values
  ('00000000-0000-0000-0000-0000000000e1', 'ok', 'Вода есть', 'Подача воды идёт или возобновилась', 'ok', 'drop', 1),
  ('00000000-0000-0000-0000-0000000000e1', 'none', 'Воды нет', 'Подача воды прекращена', 'none', 'cross', 2);

insert into map_objects (id, object_type_id, name, coverage_kind, geometry) values
  ('00000000-0000-0000-0000-0000000000f1', '00000000-0000-0000-0000-0000000000b1', 'Калининский район', 'polygon',
   'POLYGON((37.78 48.00, 37.86 48.00, 37.86 48.05, 37.78 48.05, 37.78 48.00))'::geography);
insert into map_object_sources (map_object_id, source_id) values
  ('00000000-0000-0000-0000-0000000000f1', '00000000-0000-0000-0000-0000000000e1');
