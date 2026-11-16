using BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Api.Host.Seeding;

/// <summary>
/// Идемпотентный dev/test-seed каталога, источников и фиксации из <c>db/seed_dev.sql</c>
///: категория «Вода», тип объекта «Район», статусы справочника «Вода есть»/
/// «Воды нет», источник «Чат жильцов Калининского» с политикой 6 часов и двумя статусами,
/// фиксация «Калининский район» с геометрией и привязкой к источнику. GUID совпадают с файлом.
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
    /// <c>db/seed_dev.sql</c> (строки 21–45) одной командой. Всё, что удалено через
    /// интерфейс, повторно не сеется: удаление оставляет запись в append-only журнале
    /// аудита, и по ней сид понимает, что запись убрали осознанно.
    /// </summary>
    private const string SeedSql =
        """
        do $$
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
          if exists (select 1 from categories where id = '00000000-0000-0000-0000-0000000000c1') then
            if not exists (
              select 1 from audit_entries
              where action = 'catalog.status.delete'
                and entity_type = 'status_definition'
                and before ->> 'code' = 'ok'
                and before ->> 'categoryId' = '00000000-0000-0000-0000-0000000000c1'
            ) then
              insert into status_definitions (category_id, code, name, color, icon, severity, is_positive) values
                ('00000000-0000-0000-0000-0000000000c1', 'ok', 'Вода есть', 'ok', 'drop', 0, true)
              on conflict do nothing;
            end if;

            if not exists (
              select 1 from audit_entries
              where action = 'catalog.status.delete'
                and entity_type = 'status_definition'
                and before ->> 'code' = 'none'
                and before ->> 'categoryId' = '00000000-0000-0000-0000-0000000000c1'
            ) then
              insert into status_definitions (category_id, code, name, color, icon, severity, is_positive) values
                ('00000000-0000-0000-0000-0000000000c1', 'none', 'Воды нет', 'none', 'cross', 3, false)
              on conflict do nothing;
            end if;
          end if;

          -- Источник «Чат жильцов Калининского» со статусами и политикой.
          if not exists (
            select 1 from audit_entries
            where action = 'source.delete'
              and entity_type = 'source'
              and entity_id = '00000000-0000-0000-0000-0000000000e1'
          ) then
            insert into sources (id, name, source_type, settings, user_prompt) values
              ('00000000-0000-0000-0000-0000000000e1', 'Чат жильцов Калининского', 'telegram_chat',
               '{{"chatId": "@kalininsky_water", "mode": "bot"}}',
               'Ищи в чате все упоминания про подачу и прекращение подачи воды.')
            on conflict do nothing;

            insert into source_policies (source_id, timeout) values
              ('00000000-0000-0000-0000-0000000000e1', interval '6 hours')
            on conflict do nothing;

            insert into source_statuses (source_id, code, name, description, color, icon, sort_order) values
              ('00000000-0000-0000-0000-0000000000e1', 'ok', 'Вода есть', 'Подача воды идёт или возобновилась', 'ok', 'drop', 1),
              ('00000000-0000-0000-0000-0000000000e1', 'none', 'Воды нет', 'Подача воды прекращена', 'none', 'cross', 2)
            on conflict do nothing;
          end if;

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

          -- Демо-фиксация «Калининский район».
          if not exists (
            select 1 from audit_entries
            where action = 'catalog.object.delete'
              and entity_type = 'map_object'
              and entity_id = '00000000-0000-0000-0000-0000000000f1'
          ) and exists (select 1 from object_types where id = '00000000-0000-0000-0000-0000000000b1')
          then
            insert into map_objects (id, object_type_id, name, coverage_kind, geometry) values
              ('00000000-0000-0000-0000-0000000000f1', '00000000-0000-0000-0000-0000000000b1', 'Калининский район', 'polygon',
               'POLYGON((37.78 48.00, 37.86 48.00, 37.86 48.05, 37.78 48.05, 37.78 48.00))'::geography)
            on conflict do nothing;
          end if;

          if exists (select 1 from sources where id = '00000000-0000-0000-0000-0000000000e1')
             and exists (select 1 from map_objects where id = '00000000-0000-0000-0000-0000000000f1')
          then
            insert into map_object_sources (map_object_id, source_id) values
              ('00000000-0000-0000-0000-0000000000f1', '00000000-0000-0000-0000-0000000000e1')
            on conflict do nothing;
          end if;
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
