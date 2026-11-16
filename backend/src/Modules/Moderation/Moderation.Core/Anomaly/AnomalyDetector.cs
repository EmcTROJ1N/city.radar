using System.Data;
using System.Data.Common;
using System.Globalization;

using BuildingBlocks.Persistence;

using Catalog.Contracts;

using IdentityAccess.Contracts;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Moderation.Contracts;
using Moderation.Core.Domain;

namespace Moderation.Core.Anomaly;

/// <summary>
/// Детектор всплеска отметок по «району».
/// </summary>
/// <remarks>
/// <para>
/// «Район» — это <c>group_id</c> фиксации. Если группа не задана, районом считается категория
/// типа объекта (всплеск по категории); если у фиксации нет ни группы, ни типа — отдельный
/// ключ <c>no_group</c>, чтобы такие отметки не смешивались с другими районами.
/// </para>
/// <para>
/// Считаются только пользовательские наблюдения (<c>source_type = 'manual'</c>) и только
/// неотозванные; наблюдения модераторов (<c>moderator</c>) в аномалию не попадают. Окно
/// отсчитывается по <c>observations.received_at</c> — серверному времени приёма отметки: оно
/// не зависит от клиента и не «стареет» при повторной доставке outbox-события.
/// </para>
/// <para>
/// Порог и окно читаются из <see cref="IGlobalSettings"/> на каждой проверке, поэтому смена
/// настроек действует без перезапуска процесса. Если за окно больше порога, создаётся один
/// кейс <c>anomaly</c> с <c>proposed = { count, windowSeconds, groupId, categoryId, key }</c>;
/// пока он открыт, повторные события на тот же ключ второй кейс не создают.
/// </para>
/// <para>
/// Гонка событий исключена транзакционным advisory lock по ключу района
/// (<c>pg_advisory_xact_lock(hashtext(key))</c>) плюс частичный уникальный индекс
/// <c>moderation_cases_open_anomaly_key_uq</c> по <c>proposed->&gt;&gt;'key'</c> для открытых
/// кейсов <c>anomaly</c>. После закрытия кейса (state ≠ open) новый всплеск создаёт новый.
/// </para>
/// </remarks>
public sealed class AnomalyDetector(
    MayakDbContext db,
    IModerationCaseCreator creator,
    IGlobalSettings? settings,
    IMapObjectDirectory? mapObjects)
{
    private const string NoGroupKey = "no_group";

    /// <summary>
    /// Значения по умолчанию, если <see cref="IGlobalSettings"/> не зарегистрирован в контейнере
    /// (минимальные тестовые/диагностические контейнеры без IdentityAccess). Совпадают с
    /// <c>db/seed.sql</c> и реестром настроек; в Api сервис всегда есть.
    /// </summary>
    private static readonly GlobalSettingsSnapshot DefaultSettings = new(
        ObservationRatePerHour: 20,
        CommentMaxLength: 500,
        ConfidenceThresholdDefault: 0.7,
        AnomalyWindowMinutes: 5,
        AnomalyThreshold: 30,
        FreshnessFreshRatio: 0.5,
        ProxySecretId: null);

    /// <summary>
    /// Проверяет всплеск для фиксации и создаёт кейс, если он нужен.
    /// </summary>
    /// <param name="groupId">
    /// Группа фиксации из события <c>ObservationCreated</c>; <c>null</c> — берётся текущая
    /// группа фиксации (или категория её типа, если группы нет).
    /// </param>
    /// <param name="mapObjectId">Фиксация, по которой пришло событие.</param>
    /// <param name="now">Момент проверки; окно считается назад от него.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Идентификатор созданного кейса или <c>null</c>, если всплеска нет/кейс уже открыт.</returns>
    public async Task<Guid?> CheckAsync(
        Guid? groupId,
        Guid mapObjectId,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (mapObjects is null)
        {
            // В контейнере нет каталога: район не определить, проверять нечего.
            return null;
        }

        var objectInfo = await mapObjects.GetInfoAsync(mapObjectId, ct);
        if (objectInfo is null)
        {
            return null;
        }

        var effectiveGroupId = groupId ?? objectInfo.GroupId;
        var categoryId = effectiveGroupId is null
            ? await ReadCategoryIdAsync(mapObjectId, ct)
            : null;
        var key = BuildKey(effectiveGroupId, categoryId);

        var snapshot = settings is null
            ? DefaultSettings
            : await settings.GetAsync(ct);
        var window = TimeSpan.FromMinutes(snapshot.AnomalyWindowMinutes);
        var windowStart = now - window;

        var ownsTransaction = db.Database.CurrentTransaction is null;
        var transaction = db.Database.CurrentTransaction
            ?? await db.Database.BeginTransactionAsync(ct);

        try
        {
            // Сериализует проверки одного района: параллельные события видят уже созданный кейс.
            await AcquireKeyLockAsync(key, ct);

            var count = await CountManualObservationsAsync(
                key,
                effectiveGroupId,
                categoryId,
                windowStart,
                now,
                ct);

            if (count <= snapshot.AnomalyThreshold || await OpenCaseExistsAsync(key, ct))
            {
                if (ownsTransaction)
                {
                    await transaction.CommitAsync(ct);
                }

                return null;
            }

            var caseId = await creator.CreateAsync(
                new NewModerationCase(
                    ModerationCaseKinds.Anomaly,
                    SourceId: null,
                    RawMessageId: null,
                    MapObjectId: mapObjectId,
                    ObservationId: null,
                    ReporterId: null,
                    Comment: null,
                    Proposed: new
                    {
                        count,
                        windowSeconds = (int)window.TotalSeconds,
                        groupId = effectiveGroupId,
                        categoryId,
                        key,
                    }),
                ct);

            await db.SaveChangesAsync(ct);

            if (ownsTransaction)
            {
                await transaction.CommitAsync(ct);
            }

            return caseId;
        }
        catch
        {
            if (ownsTransaction)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw;
        }
        finally
        {
            if (ownsTransaction)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    /// <summary>Ключ района, по которому дедуплицируются открытые кейсы.</summary>
    private static string BuildKey(Guid? groupId, Guid? categoryId) =>
        groupId is { } group
            ? $"group:{group}"
            : categoryId is { } category
                ? $"category:{category}"
                : NoGroupKey;

    /// <summary>
    /// Категория типа объекта; <c>null</c> — у фиксации нет типа или типа нет в справочнике.
    /// </summary>
    private async Task<Guid?> ReadCategoryIdAsync(Guid mapObjectId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;

        if (closeAfter)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText =
                """
                select object_type.category_id
                from map_objects map_object
                join object_types object_type on object_type.id = map_object.object_type_id
                where map_object.id = @map_object
                """;
            AddParameter(command, "map_object", mapObjectId);

            var value = await command.ExecuteScalarAsync(ct);

            return value is Guid categoryId ? categoryId : null;
        }
        finally
        {
            if (closeAfter)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    /// <summary>Транзакционный advisory lock по ключу района (снимается коммитом/откатом).</summary>
    private async Task AcquireKeyLockAsync(string key, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;

        if (closeAfter)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = "select pg_advisory_xact_lock(hashtext(@key))";
            AddParameter(command, "key", key);

            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (closeAfter)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    /// <summary>
    /// Число неотозванных пользовательских наблюдений района за окно
    /// <c>(windowStart, now]</c> по <c>received_at</c>.
    /// </summary>
    private async Task<long> CountManualObservationsAsync(
        string key,
        Guid? groupId,
        Guid? categoryId,
        DateTimeOffset windowStart,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var filter = key switch
        {
            NoGroupKey =>
                """
                exists (
                  select 1
                  from map_objects map_object
                  where map_object.id = observation.map_object_id
                    and map_object.group_id is null
                    and map_object.object_type_id is null)
                """,
            _ when groupId is not null =>
                """
                exists (
                  select 1
                  from map_objects map_object
                  where map_object.id = observation.map_object_id
                    and map_object.group_id = @group_id)
                """,
            _ =>
                """
                exists (
                  select 1
                  from map_objects map_object
                  join object_types object_type on object_type.id = map_object.object_type_id
                  where map_object.id = observation.map_object_id
                    and object_type.category_id = @category_id)
                """,
        };

        var connection = db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;

        if (closeAfter)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText =
                $"""
                 select count(*)
                 from observations observation
                 where observation.source_type = 'manual'
                   and not observation.is_retracted
                   and observation.received_at > @window_start
                   and observation.received_at <= @now
                   and {filter}
                 """;
            AddParameter(command, "window_start", windowStart);
            AddParameter(command, "now", now);

            if (groupId is { } group)
            {
                AddParameter(command, "group_id", group);
            }

            if (categoryId is { } category)
            {
                AddParameter(command, "category_id", category);
            }

            var count = await command.ExecuteScalarAsync(ct);

            return Convert.ToInt64(count, CultureInfo.InvariantCulture);
        }
        finally
        {
            if (closeAfter)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    /// <summary>Есть ли уже открытый кейс <c>anomaly</c> на этот ключ района.</summary>
    private async Task<bool> OpenCaseExistsAsync(string key, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;

        if (closeAfter)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText =
                """
                select count(*)
                from moderation_cases
                where kind = 'anomaly'
                  and state = 'open'
                  and proposed->>'key' = @key
                """;
            AddParameter(command, "key", key);

            var count = await command.ExecuteScalarAsync(ct);

            return Convert.ToInt64(count, CultureInfo.InvariantCulture) > 0;
        }
        finally
        {
            if (closeAfter)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
