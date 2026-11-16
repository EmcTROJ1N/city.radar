namespace Ingestion.Abstractions;

/// <summary>
/// Контракт адаптера источника данных: запуск и остановка чтения, поток нормализованных
/// сообщений и health-check. Смена мессенджера или формата — это новый адаптер, pipeline не меняется.
/// </summary>
public interface IDataSource : IAsyncDisposable
{
    /// <summary>Идентификатор источника, который обслуживает адаптер.</summary>
    Guid SourceId { get; }

    /// <summary>Запускает чтение сообщений источника.</summary>
    /// <param name="ct">Токен отмены запуска.</param>
    Task StartAsync(CancellationToken ct);

    /// <summary>Останавливает чтение сообщений; повторный вызов допустим.</summary>
    /// <param name="ct">Токен отмены остановки.</param>
    Task StopAsync(CancellationToken ct);

    /// <summary>
    /// Поток сообщений источника. Адаптер отдаёт сообщения по мере поступления; после
    /// <see cref="StopAsync"/> поток завершается.
    /// </summary>
    /// <param name="ct">Токен отмены чтения.</param>
    IAsyncEnumerable<RawMessage> ReadAsync(CancellationToken ct);

    /// <summary>Проверяет подключение и возвращает текущее состояние источника.</summary>
    /// <param name="ct">Токен отмены проверки.</param>
    Task<SourceHealth> CheckHealthAsync(CancellationToken ct);
}
