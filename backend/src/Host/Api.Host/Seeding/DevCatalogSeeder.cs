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
/// </remarks>
public sealed class DevCatalogSeeder(MayakDbContext db)
{
    /// <summary>
    /// Эквивалент блоков «Базовый каталог воды» и «Источник «Вода» …» из
    /// <c>db/seed_dev.sql</c> (строки 21–45) одной командой.
    /// </summary>
    private const string SeedSql =
        """
        insert into categories (id, code, name, icon, sort_order) values
          ('00000000-0000-0000-0000-0000000000c1', 'water', 'Вода', 'drop', 1)
        on conflict do nothing;

        insert into object_types (id, category_id, code, name, geometry_kind) values
          ('00000000-0000-0000-0000-0000000000b1', '00000000-0000-0000-0000-0000000000c1', 'district', 'Район', 'polygon')
        on conflict do nothing;

        insert into status_definitions (category_id, code, name, color, icon, severity, is_positive) values
          ('00000000-0000-0000-0000-0000000000c1', 'ok', 'Вода есть', 'ok', 'drop', 0, true),
          ('00000000-0000-0000-0000-0000000000c1', 'none', 'Воды нет', 'none', 'cross', 3, false)
        on conflict do nothing;

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

        insert into map_objects (id, object_type_id, name, coverage_kind, geometry) values
          ('00000000-0000-0000-0000-0000000000f1', '00000000-0000-0000-0000-0000000000b1', 'Калининский район', 'polygon',
           'POLYGON((37.78 48.00, 37.86 48.00, 37.86 48.05, 37.78 48.05, 37.78 48.00))'::geography)
        on conflict do nothing;

        insert into map_object_sources (map_object_id, source_id) values
          ('00000000-0000-0000-0000-0000000000f1', '00000000-0000-0000-0000-0000000000e1')
        on conflict do nothing;
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
