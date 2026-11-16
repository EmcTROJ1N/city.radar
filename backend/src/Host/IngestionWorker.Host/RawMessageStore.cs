using BuildingBlocks.Persistence;

using Ingestion.Abstractions;

using Microsoft.EntityFrameworkCore.Storage;

namespace IngestionWorker.Host;

/// <summary>
/// Сохранение сырых сообщений источника в <c>raw_messages</c> (шаг «RawMessage»).
/// Доступ — сырым SQL: Worker-у не нужны internal-типы модуля Observations, а уникальность
/// <c>(source_id, external_id)</c> обеспечивает идемпотентность обработки.
/// </summary>
internal sealed class RawMessageStore(MayakDbContext db)
{
    private const string InsertSql =
        """
        insert into raw_messages (source_id, external_id, text, author, chat, link, posted_at)
        values (@source_id, @external_id, @text, @author, @chat, @link, @posted_at)
        on conflict (source_id, external_id) do nothing
        returning id
        """;

    /// <summary>
    /// Пытается вставить сообщение в текущей транзакции. Возвращает идентификатор новой записи
    /// либо <c>null</c>, если сообщение с таким <c>external_id</c> уже обработано: повторная
    /// доставка не должна гонять pipeline второй раз.
    /// </summary>
    public async Task<Guid?> TryInsertAsync(
        RawMessage message,
        IDbContextTransaction transaction,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(transaction);

        await using var command = await WorkerSql.CreateCommandAsync(db, InsertSql, ct)
            .ConfigureAwait(false);
        command.Transaction = transaction.GetDbTransaction();

        WorkerSql.AddParameter(command, "source_id", message.SourceId);
        WorkerSql.AddParameter(command, "external_id", message.ExternalId);
        WorkerSql.AddParameter(command, "text", message.Text);
        WorkerSql.AddParameter(command, "author", message.Author);
        WorkerSql.AddParameter(command, "chat", message.Chat);
        WorkerSql.AddParameter(command, "link", message.Link);
        WorkerSql.AddParameter(command, "posted_at", message.PostedAt);

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        return result is Guid id ? id : null;
    }
}
