using System.Data;
using System.Data.Common;

using BuildingBlocks.Persistence;
using BuildingBlocks.Web;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MapReadModel.Core;

/// <summary>
/// Источник версии состояния карты: один SQL-запрос по таблицам, влияющим на выдачу
/// <c>/map/features</c>. Отделён от <see cref="MapVersionProvider"/> ради тестирования
/// кэша без базы данных.
/// </summary>
public interface IMapVersionSource
{
    /// <summary>Читает актуальную версию состояния карты из хранилища.</summary>
    /// <param name="ct">Токен отмены.</param>
    Task<string> ReadAsync(CancellationToken ct = default);
}

/// <summary>
/// Считает версию состояния карты одним read-only SQL-запросом (Review Focus 3).
/// </summary>
/// <remarks>
/// Версия обязана меняться от любой правки, заметной в <c>GET /map/features</c>:
/// <list type="bullet">
///   <item><description>фиксации — <c>max(updated_at)</c> + <c>count(*)</c> + хеш
///   <c>id|updated_at</c>: имя, геометрия, группа, <c>is_active</c>, создание и удаление
///   (триггер <c>set_updated_at</c>); хеш строк нужен, потому что <c>max</c> не меняется
///   при правке фиксации, которая не самая свежая в таблице;</description></item>
///   <item><description>проекция <c>current_statuses</c> — <c>max(updated_at)</c> + <c>count(*)</c>:
///   новая проекция, смена победившего статуса, отзыв фиксации, истечение таймаута;</description></item>
///   <item><description><c>source_statuses</c> — изменяемый набор без собственного
///   <c>updated_at</c>, поэтому хеш строк (код, название, цвет, иконка, описание, порядок):
///   правка цвета/иконки/названия статуса меняет вид карты;</description></item>
///   <item><description><c>object_groups</c>, <c>categories</c>, <c>object_types</c> — хеш строк:
///   от них зависят фильтры и подписи групп;</description></item>
///   <item><description><c>current_statuses</c> — дополнительно хеш строк, чтобы правка
///   проекции без изменения <c>updated_at</c> (ручной SQL, миграция данных) не проходила
///   незамеченной.</description></item>
/// </list>
/// Хеширование агрегатов детерминировано (<c>order by id</c>) и не зависит от порядка строк
/// в таблице. Запрос читает только агрегаты, поэтому стоимость не растёт с числом фиксаций
/// так, как полная выборка: его результат кэшируется на секунду в
/// <see cref="MapVersionProvider"/>.
/// </remarks>
public sealed class MapVersionSource(IServiceScopeFactory scopeFactory) : IMapVersionSource
{
    private const string VersionSql = """
        select
            (select coalesce(max(updated_at)::text, '') from current_statuses),
            (select count(*)::text from current_statuses),
            (select coalesce(md5(string_agg(row_to_json(s)::text, ',' order by s.id)), '')
               from (select map_object_id as id, source_id, source_status_id, observation_id, observed_at,
                            freshness, source_type, observation_count, updated_at
                     from current_statuses) s),
            (select coalesce(max(updated_at)::text, '') from map_objects),
            (select count(*)::text from map_objects),
            (select coalesce(md5(string_agg(id::text || '|' || updated_at::text, ',' order by id)), '')
               from map_objects),
            (select coalesce(md5(string_agg(row_to_json(s)::text, ',' order by s.id)), '')
               from (select id, source_id, code, name, description, color, icon, sort_order
                     from source_statuses) s),
            (select coalesce(md5(string_agg(row_to_json(g)::text, ',' order by g.id)), '')
               from (select id, category_id, name, color, is_visible_by_default
                     from object_groups) g),
            (select coalesce(md5(string_agg(row_to_json(c)::text, ',' order by c.id)), '')
               from (select id, code, name, icon, sort_order, is_active
                     from categories) c),
            (select coalesce(md5(string_agg(row_to_json(t)::text, ',' order by t.id)), '')
               from (select id, category_id, code, name, geometry_kind
                     from object_types) t)
        """;

    /// <inheritdoc />
    public async Task<string> ReadAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MayakDbContext>();
        var connection = db.Database.GetDbConnection();
        var openedHere = false;

        try
        {
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(ct);
                openedHere = true;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = VersionSql;

            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                throw new InvalidOperationException("Запрос версии карты не вернул строку.");
            }

            var parts = new string[reader.FieldCount];
            for (var index = 0; index < parts.Length; index++)
            {
                parts[index] = reader.IsDBNull(index) ? string.Empty : reader.GetString(index);
            }

            return ETagHelper.ComputeHash(string.Join('\n', parts));
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }
}

/// <summary>
/// Версия состояния карты с секундным кэшем в памяти.
/// </summary>
/// <remarks>
/// Карта опрашивается каждые 30–60 с множеством клиентов, поэтому один и тот же запрос версии
/// не должен уходить в БД на каждый HTTP-запрос. Кэш живёт <see cref="CacheDuration"/>; при
/// истечении ровно одна загрузка выполняется через <see cref="SemaphoreSlim"/>, остальные
/// параллельные вызовы ждут её результат — «лавины» запросов нет.
/// </remarks>
public sealed class MapVersionProvider(IMapVersionSource source, TimeProvider time)
{
    /// <summary>Срок жизни закэшированной версии.</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(1);

    private readonly object _sync = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private string? _cachedVersion;
    private DateTimeOffset _expiresAt;

    /// <summary>
    /// Возвращает версию состояния карты; при истёкшем кэше перечитывает её из
    /// <see cref="IMapVersionSource"/>. Одна параллельная загрузка на истечение.
    /// </summary>
    /// <param name="ct">Токен отмены ожидания загрузки.</param>
    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        if (TryGetCached(out var cached))
        {
            return cached;
        }

        await _loadGate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // Пока ждали семафор, другая загрузка могла обновить кэш.
            if (TryGetCached(out cached))
            {
                return cached;
            }

            var version = await source.ReadAsync(ct).ConfigureAwait(false);

            lock (_sync)
            {
                _cachedVersion = version;
                _expiresAt = time.GetUtcNow() + CacheDuration;
            }

            return version;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private bool TryGetCached(out string version)
    {
        lock (_sync)
        {
            if (_cachedVersion is not null && time.GetUtcNow() < _expiresAt)
            {
                version = _cachedVersion;

                return true;
            }
        }

        version = string.Empty;

        return false;
    }
}
