using BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Api.Host.Seeding;

/// <summary>
/// Идемпотентный dev/test-seed каталога, источников и фиксаций из <c>db/seed_dev.sql</c>
///: категория «Вода», тип объекта «Район», шесть статусов справочника, демо-источники
/// «Чат жильцов Калининского» и «Новости ТП «Шахтёрский-2»» со статусами и промптом своей зоны,
/// геозоны «Калининский район» и «Шахтёрский мкрн» с привязками к источникам.
/// GUID совпадают с файлом.
/// </summary>
/// <remarks>
/// Сид каталога живёт в composition root (<c>Api.Host</c>), а не в
/// <c>IdentityAccess.Core</c>: <c>IdentityAccess</c> не должен писать в таблицы чужих модулей
///. Порядок запуска — сразу после <see cref="IdentityAccess.Core.Seeding.IdentitySeedRunner"/>;
/// <c>created_by</c> в <c>db/seed_dev.sql</c> не задаётся, поэтому пользователи формально не
/// нужны, но к моменту вызова они уже созданы. Каждый <c>insert</c> защищён
/// <c>on conflict do nothing</c>: повторный запуск ничего не дублирует.
/// Удалённые через интерфейс источник и фиксация не возвращаются: удаление пишется в
/// append-only журнал аудита, и сид считает такую запись «больше не сеять».
/// </remarks>
public sealed class DevCatalogSeeder(MayakDbContext db)
{
    /// <summary>
    /// Эквивалент блоков «Базовый каталог воды» и «Источник «Вода» …» из
    /// <c>db/seed_dev.sql</c> одной командой. Всё, что удалено через
    /// интерфейс, повторно не сеется: удаление оставляет запись в append-only журнале
    /// аудита, и по ней сид понимает, что запись убрали осознанно.
    /// </summary>
    private const string SeedSql =
        """
        do $$
        declare
          -- Общие правила разбора для демо-источников «Вода»; перед ними — строка зоны источника.
          water_rules text := 'Ищи сообщения о подаче холодной воды в квартиры и во врезки (краны у подъездов).
        Если адрес не указан («у нас», «тоже», «пошла»), считай, что сообщение о зоне источника.

        Как выбрать статус:
        - «пошла», «идёт», «дали» — ok; «вода всё», «закончилась», «пропала», «перекрыли» — none.
        - Вода есть только на нижних этажах, а на верхних нет, или есть только во врезке, а в квартирах нет — partial.
          Вода во врезке не означает, что она есть в квартирах.
        - «Струйка», «ниточка», «в пол силы», «еле-еле», «напор слабый» — weak.
        - «Кап-кап», «спичка», «пару капель», «течёт самотёком» (остатки из труб) — это none, а не weak.
        - Вода идёт, но ржавая, жёлтая, рыжая, мутная или с запахом — dirty.
        - Прорыв, ремонт трубы, перекрыли задвижку, на месте водоканал или аварийка — outage.
        - Отсутствие воды без слова «нет» — тоже none: «водичкой не пахнет», «тишина в кране», «признаков жизни нет»,
          «не журчит», «нет даже намёка».

        Не является статусом (relevant = false):
        - Вопросы: «есть вода?», «у кого пошла?», «сегодня день воды?».
        - Прогнозы, обещания и слухи: «будет после 18», «должна дойти», «водоканал сказал завтра»,
          «на соседней улице пошла, значит через час и у нас».
        - Прошлое: «вчера была», «в прошлый раз шла ржавая».
        - Отопление: батареи, стояки отопления, «вода в системе», «развоздушить», «кипяток в стояке».
        - Электричество, свет и напряжение, если о воде в сообщении ничего не сказано.
        - Графики подачи, новости и репосты, объявления, сборы денег, поиск мастеров, споры и жалобы без состояния воды.
        - Сообщения, где явно назван другой район, микрорайон или улица за пределами зоны источника.';
        begin
          -- Категория «Вода».
          if not exists (
            select 1 from audit_entries
            where action = 'catalog.category.delete'
              and entity_type = 'category'
              and entity_id = '00000000-0000-0000-0000-0000000000c1'
          ) then
            insert into categories (id, code, name, icon, sort_order) values
              ('00000000-0000-0000-0000-0000000000c1', 'water', 'Вода', 'drop', 1)
            on conflict do nothing;
          end if;

          -- Тип объекта «Район».
          if not exists (
            select 1 from audit_entries
            where action = 'catalog.objecttype.delete'
              and entity_type = 'object_type'
              and entity_id = '00000000-0000-0000-0000-0000000000b1'
          ) and exists (select 1 from categories where id = '00000000-0000-0000-0000-0000000000c1')
          then
            insert into object_types (id, category_id, code, name, geometry_kind) values
              ('00000000-0000-0000-0000-0000000000b1', '00000000-0000-0000-0000-0000000000c1', 'district', 'Район', 'polygon')
            on conflict do nothing;
          end if;

          -- Статусы справочника: id генерируется, поэтому удаление ищем по коду.
          insert into status_definitions (category_id, code, name, color, icon, severity, is_positive)
          select '00000000-0000-0000-0000-0000000000c1', v.code, v.name, v.color, v.icon, v.severity, v.is_positive
          from (values
            ('ok', 'Вода есть', 'ok', 'drop', 0, true),
            ('partial', 'Частично', 'limited', 'alert', 1, false),
            ('weak', 'Слабый напор', 'limited', 'drop', 2, false),
            ('dirty', 'Есть, но грязная', 'limited', 'alert', 2, false),
            ('none', 'Воды нет', 'none', 'cross', 3, false),
            ('outage', 'Авария / ремонт', 'none', 'alert', 4, false)
          ) as v(code, name, color, icon, severity, is_positive)
          where exists (select 1 from categories where id = '00000000-0000-0000-0000-0000000000c1')
            and not exists (
              select 1 from audit_entries
              where action = 'catalog.status.delete'
                and entity_type = 'status_definition'
                and before ->> 'code' = v.code
                and before ->> 'categoryId' = '00000000-0000-0000-0000-0000000000c1'
            )
          on conflict do nothing;

          -- Демо-источники «Вода»: промпт — строка зоны и общие правила. Промпт уже существующего
          -- источника не перезаписывается; недостающие статусы добавляются.
          insert into sources (id, name, source_type, settings, user_prompt)
          select v.id, v.name, 'telegram_chat', v.settings::jsonb, v.zone || E'\n\n' || water_rules
          from (values
            ('00000000-0000-0000-0000-0000000000e1'::uuid, 'Чат жильцов Калининского',
             '{{"chatId": "@kalininsky_water", "mode": "bot"}}',
             'Зона источника — Калининский район Донецка.'),
            ('00000000-0000-0000-0000-0000000000e3'::uuid, 'Новости ТП «Шахтёрский-2»',
             '{{"chatId": "@shakhtersky2_news", "mode": "bot"}}',
             'Зона источника — микрорайон Шахтёрский в Донецке (ТП «Шахтёрский-2»): ул. Батищева, ул. Артёма 150а–154в, ул. Университетская 132–136.')
          ) as v(id, name, settings, zone)
          where not exists (
            select 1 from audit_entries
            where action = 'source.delete'
              and entity_type = 'source'
              and entity_id = v.id::text
          )
          on conflict do nothing;

          insert into source_policies (source_id, timeout)
          select s.id, interval '6 hours'
          from sources s
          where s.id in ('00000000-0000-0000-0000-0000000000e1', '00000000-0000-0000-0000-0000000000e3')
          on conflict do nothing;

          insert into source_statuses (source_id, code, name, description, color, icon, sort_order)
          select s.id, v.code, v.name, v.description, v.color, v.icon, v.sort_order
          from sources s
          cross join (values
            ('ok', 'Вода есть', 'Вода идёт с нормальным напором, в том числе на верхних этажах', 'ok', 'drop', 1),
            ('partial', 'Частично', 'Вода есть не у всех: только на нижних этажах, в части домов или только во врезке', 'limited', 'alert', 2),
            ('weak', 'Слабый напор', 'Вода идёт, но слабо: струйка, ниточка, в пол силы', 'limited', 'drop', 3),
            ('dirty', 'Есть, но грязная', 'Вода идёт, но ржавая, жёлтая, мутная или с запахом', 'limited', 'alert', 4),
            ('none', 'Воды нет', 'Воды нет, закончилась или только капает', 'none', 'cross', 5),
            ('outage', 'Авария / ремонт', 'Прорыв, ремонт, перекрыта задвижка', 'none', 'alert', 6)
          ) as v(code, name, description, color, icon, sort_order)
          where s.id in ('00000000-0000-0000-0000-0000000000e1', '00000000-0000-0000-0000-0000000000e3')
          on conflict do nothing;

          -- Системный источник «Сайт» категории «Вода»: политика и статусы из справочника.
          if not exists (
            select 1 from audit_entries
            where action = 'source.delete'
              and entity_type = 'source'
              and entity_id = '00000000-0000-0000-0000-0000000000e2'
          ) and exists (select 1 from categories where id = '00000000-0000-0000-0000-0000000000c1')
          then
            insert into sources
              (id, name, source_type, category_id, settings, user_prompt, link_by_text)
            values
              ('00000000-0000-0000-0000-0000000000e2', 'Сайт · Вода', 'site',
               '00000000-0000-0000-0000-0000000000c1', '{{}}', '', false)
            on conflict do nothing;

            insert into source_policies (source_id, timeout)
            select '00000000-0000-0000-0000-0000000000e2', interval '3 hours'
            where exists (select 1 from sources where id = '00000000-0000-0000-0000-0000000000e2')
            on conflict do nothing;

            insert into source_statuses
              (source_id, status_definition_id, code, name, description, color, icon, sort_order)
            select '00000000-0000-0000-0000-0000000000e2', sd.id, sd.code, sd.name, '',
                   sd.color, sd.icon, sd.severity
            from status_definitions sd
            where sd.category_id = '00000000-0000-0000-0000-0000000000c1'
              and exists (select 1 from sources where id = '00000000-0000-0000-0000-0000000000e2')
            on conflict (source_id, code) do nothing;
          end if;

          -- Демо-геозоны с источниками: «Калининский район» и «Шахтёрский мкрн» (участок между
          -- демо-геозонами из db/seed_demo_geozones.sql). Точки (дома, врезки) создаёт модератор.
          insert into map_objects (id, object_type_id, name, coverage_kind, geometry)
          select v.id, '00000000-0000-0000-0000-0000000000b1', v.name, 'polygon', v.geometry::geography
          from (values
            ('00000000-0000-0000-0000-0000000000f1'::uuid, 'Калининский район',
             'POLYGON((37.78 48.00, 37.86 48.00, 37.86 48.05, 37.78 48.05, 37.78 48.00))'),
            ('00000000-0000-0000-0000-0000000000f2'::uuid, 'Шахтёрский мкрн',
             'POLYGON((37.77520 48.03684, 37.77570 48.03546, 37.77837 48.03589, 37.77814 48.03666, 37.77760 48.03667, 37.77520 48.03684))')
          ) as v(id, name, geometry)
          where exists (select 1 from object_types where id = '00000000-0000-0000-0000-0000000000b1')
            and not exists (
              select 1 from audit_entries
              where action = 'catalog.object.delete'
                and entity_type = 'map_object'
                and entity_id = v.id::text
            )
          on conflict do nothing;

          insert into map_object_sources (map_object_id, source_id)
          select v.map_object_id, v.source_id
          from (values
            ('00000000-0000-0000-0000-0000000000f1'::uuid, '00000000-0000-0000-0000-0000000000e1'::uuid),
            ('00000000-0000-0000-0000-0000000000f2'::uuid, '00000000-0000-0000-0000-0000000000e3'::uuid)
          ) as v(map_object_id, source_id)
          where exists (select 1 from map_objects where id = v.map_object_id)
            and exists (select 1 from sources where id = v.source_id)
          on conflict do nothing;
        end $$;
        """;

    /// <summary>
    /// Создаёт недостающие записи каталога, источника и фиксации. Повторный вызов ничего
    /// не дублирует и существующие записи не изменяет.
    /// </summary>
    public async Task SeedAsync(CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync(SeedSql, ct);
    }
}
