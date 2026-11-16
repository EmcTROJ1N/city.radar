using Ingestion.Abstractions;

namespace Sources.Contracts;

/// <summary>
/// Запись результата health-check источника. Вызывается Worker'ом
/// после подключения, проверки и обработки ошибок адаптера; Api только читает состояние.
/// Health — служебные данные: запись идёт в таблицу <c>sources</c> без записи в аудит
/// (журнал аудита предназначен для действий модераторов и админов).
/// </summary>
public interface ISourceHealthReporter
{
    /// <summary>
    /// Сохраняет состояние источника и время последнего полученного сообщения.
    /// </summary>
    /// <param name="sourceId">Источник; для уже удалённого источника вызов игнорируется.</param>
    /// <param name="state">
    /// Состояние из <see cref="SourceState"/>; в БД пишется значение snake_case, совпадающее
    /// с check-констрейнтом <c>sources.state</c>.
    /// </param>
    /// <param name="detail">
    /// Деталь для UI. Для <see cref="SourceState.FloodWait"/> — момент окончания ожидания
    /// в формате ISO-8601 (в ответе <c>GET /sources/{id}/health</c> попадает в
    /// <c>floodWaitUntil</c>); для <see cref="SourceState.AuthError"/> и
    /// <see cref="SourceState.Error"/> — текст ошибки (попадает в <c>lastError</c>).
    /// </param>
    /// <param name="lastMessageAt">
    /// Время последнего полученного сообщения. <c>null</c> не стирает уже известное время:
    /// Worker может сообщать health-check без нового сообщения.
    /// </param>
    /// <param name="ct">Токен отмены.</param>
    Task ReportAsync(
        Guid sourceId,
        SourceState state,
        string? detail,
        DateTimeOffset? lastMessageAt,
        CancellationToken ct);
}
