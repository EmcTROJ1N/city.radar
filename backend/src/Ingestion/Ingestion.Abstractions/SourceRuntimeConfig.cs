using System.Text.Json;

namespace Ingestion.Abstractions;

/// <summary>
/// Снимок настроек источника для создания адаптера: всё, что нужно адаптеру на момент
/// запуска. Изменения в БД применяются перезапуском конкретного адаптера, без перезапуска Worker.
/// </summary>
/// <param name="SourceId">Идентификатор источника.</param>
/// <param name="SourceType">Код типа источника из <c>source_types.code</c>.</param>
/// <param name="Settings">Настройки типа источника из <c>sources.settings</c> (например, идентификатор чата).</param>
/// <param name="SessionSecret">Расшифрованные данные сессии Telegram; <c>null</c>, если тип источника их не использует.</param>
/// <param name="Proxy">Действующий прокси (источник → аккаунт → глобальный); <c>null</c>, если прокси не задан.</param>
/// <param name="ProxyRequired">
/// <c>true</c>, если прокси задан хотя бы в одном звене цепочки: прямое соединение в этом случае
/// запрещено. Прокси может быть <c>null</c> при <c>ProxyRequired = true</c>,
/// если ссылка на секрет не разрешилась — тогда адаптер обязан сообщить ошибку и не подключаться.
/// </param>
/// <param name="ReportHealth">
/// Мост к <c>ISourceHealthReporter</c>: адаптер сообщает состояние источника при подключении,
/// ошибках, FloodWait и получении сообщений, не зная о модуле Sources. <c>null</c> в unit-тестах.
/// </param>
public sealed record SourceRuntimeConfig(
    Guid SourceId,
    string SourceType,
    JsonElement Settings,
    string? SessionSecret,
    ProxySettings? Proxy,
    bool ProxyRequired = false,
    SourceHealthCallback? ReportHealth = null)
{
    /// <summary>
    /// Представление для логов: секрет сессии не выводится, пароль прокси скрывает
    /// <see cref="ProxySettings.ToString"/>.
    /// </summary>
    public override string ToString()
    {
        var settings = Settings.ValueKind == JsonValueKind.Undefined ? "{}" : Settings.GetRawText();
        var sessionSecret = SessionSecret is null ? "null" : "***";

        return $"SourceRuntimeConfig {{ SourceId = {SourceId}, SourceType = {SourceType}, " +
               $"Settings = {settings}, SessionSecret = {sessionSecret}, Proxy = {Proxy}, " +
               $"ProxyRequired = {ProxyRequired} }}";
    }
}

/// <summary>
/// Колбэк записи health источника из адаптера. Сигнатура повторяет
/// <c>Sources.Contracts.ISourceHealthReporter.ReportAsync</c>, но интерфейс живёт в модуле
/// Sources, а адаптеры не должны зависеть от него: мост создаёт supervisor.
/// </summary>
/// <param name="state">Состояние источника.</param>
/// <param name="detail">Деталь: ISO-8601 для FloodWait, текст ошибки для auth_error/error.</param>
/// <param name="lastMessageAt">Время последнего полученного сообщения; <c>null</c> — новых не было.</param>
/// <param name="ct">Токен отмены.</param>
public delegate Task SourceHealthCallback(
    SourceState state,
    string? detail,
    DateTimeOffset? lastMessageAt,
    CancellationToken ct);
