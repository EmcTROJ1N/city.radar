-- Демонстрационные геозоны вокруг «Шахтерский мкрн» (Донецк, Киевский район).
-- Геозоны и демо-источники к ним (см. конец файла).
-- Границы — соседние микрорайоны/жилые районы по данным OSM и описаниям
-- административного деления Донецка. Скрипт идемпотентен.

insert into map_objects (id, name, coverage_kind, geometry) values
  ('00000000-0000-0000-0000-0000000000d1', 'Маяк мкрн', 'polygon',
   'POLYGON((37.77520 48.03684, 37.77090 48.03745, 37.77000 48.03560, 37.77280 48.03510, 37.77570 48.03546, 37.77520 48.03684))'::geography),
  ('00000000-0000-0000-0000-0000000000d2', 'Привокзальный мкрн', 'polygon',
   'POLYGON((37.77570 48.03546, 37.77280 48.03510, 37.77300 48.03300, 37.78250 48.03300, 37.77837 48.03589, 37.77570 48.03546))'::geography),
  ('00000000-0000-0000-0000-0000000000d3', 'Ветка мкрн', 'polygon',
   'POLYGON((37.77837 48.03589, 37.78250 48.03300, 37.78720 48.03340, 37.78610 48.03730, 37.78100 48.03745, 37.77760 48.03667, 37.77814 48.03666, 37.77837 48.03589))'::geography),
  ('00000000-0000-0000-0000-0000000000d4', 'Путиловка мкрн', 'polygon',
   'POLYGON((37.77520 48.03684, 37.77760 48.03667, 37.78100 48.03745, 37.77940 48.04180, 37.77250 48.04150, 37.77080 48.03900, 37.77090 48.03745, 37.77520 48.03684))'::geography)
on conflict (id) do update
set name = excluded.name,
    coverage_kind = excluded.coverage_kind,
    geometry = excluded.geometry,
    updated_at = now();

-- Демо-источники для геозон: по чату на микрорайон. Статусы, политика и правила разбора копируются
-- у «Чата жильцов Калининского» из seed_dev.sql (файл применяется после сида приложения),
-- первая строка промпта — зона источника.
insert into sources (id, name, source_type, settings, user_prompt)
select v.id, v.name, 'telegram_chat', jsonb_build_object('chatId', v.chat_id, 'mode', 'bot'),
       v.zone || substr(t.user_prompt, strpos(t.user_prompt, E'\n\n'))
from sources t
cross join (values
  ('00000000-0000-0000-0000-0000000000e4'::uuid, 'Чат жильцов мкрн Маяк', '@mayak_water',
   'Зона источника — микрорайон Маяк в Донецке.'),
  ('00000000-0000-0000-0000-0000000000e5'::uuid, 'Чат жильцов мкрн Привокзальный', '@privokzalny_water',
   'Зона источника — Привокзальный микрорайон в Донецке.'),
  ('00000000-0000-0000-0000-0000000000e6'::uuid, 'Чат жильцов мкрн Ветка', '@vetka_water',
   'Зона источника — микрорайон Ветка в Донецке.'),
  ('00000000-0000-0000-0000-0000000000e7'::uuid, 'Чат жильцов мкрн Путиловка', '@putilovka_water',
   'Зона источника — микрорайон Путиловка в Донецке.')
) as v(id, name, chat_id, zone)
where t.id = '00000000-0000-0000-0000-0000000000e1'
on conflict (id) do nothing;

insert into source_policies (source_id, timeout)
select s.id, interval '6 hours'
from sources s
where s.id in ('00000000-0000-0000-0000-0000000000e4', '00000000-0000-0000-0000-0000000000e5',
               '00000000-0000-0000-0000-0000000000e6', '00000000-0000-0000-0000-0000000000e7')
on conflict do nothing;

insert into source_statuses (source_id, code, name, description, color, icon, sort_order)
select s.id, t.code, t.name, t.description, t.color, t.icon, t.sort_order
from sources s
cross join source_statuses t
where t.source_id = '00000000-0000-0000-0000-0000000000e1'
  and s.id in ('00000000-0000-0000-0000-0000000000e4', '00000000-0000-0000-0000-0000000000e5',
               '00000000-0000-0000-0000-0000000000e6', '00000000-0000-0000-0000-0000000000e7')
on conflict do nothing;

insert into map_object_sources (map_object_id, source_id)
select v.map_object_id, v.source_id
from (values
  ('00000000-0000-0000-0000-0000000000d1'::uuid, '00000000-0000-0000-0000-0000000000e4'::uuid),
  ('00000000-0000-0000-0000-0000000000d2'::uuid, '00000000-0000-0000-0000-0000000000e5'::uuid),
  ('00000000-0000-0000-0000-0000000000d3'::uuid, '00000000-0000-0000-0000-0000000000e6'::uuid),
  ('00000000-0000-0000-0000-0000000000d4'::uuid, '00000000-0000-0000-0000-0000000000e7'::uuid)
) as v(map_object_id, source_id)
where exists (select 1 from sources where id = v.source_id)
on conflict do nothing;
