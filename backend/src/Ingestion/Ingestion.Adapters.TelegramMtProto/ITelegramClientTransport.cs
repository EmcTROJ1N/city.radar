using Ingestion.Abstractions;

namespace Ingestion.Adapters.TelegramMtProto;

/// <summary>
/// Параметры запуска клиента MTProto. Секрет сессии хранится как JSON
/// <c>{ apiId, apiHash, sessionData?, phone? }</c> и расшифровывается Worker-ом
/// (<c>ISecretReader</c>) до создания адаптера.
/// </summary>
/// <param name="ApiId">Идентификатор приложения Telegram (my.telegram.org).</param>
/// <param name="ApiHash">Секрет приложения Telegram; в логи не выводится.</param>
/// <param name="SessionData">Сохранённая сессия WTelegramClient (Base64); <c>null</c> — новая сессия.</param>
/// <param name="Phone">Телефон аккаунта для ручного входа; <c>null</c>, если сессия уже есть.</param>
/// <param name="Proxy">Прокси; <c>null</c> — прямое соединение разрешено.</param>
public sealed record MtProtoTransportConfig(
    Guid SourceId,
    int ApiId,
    string ApiHash,
    string? SessionData,
    string? Phone,
    ProxySettings? Proxy)
{
    /// <inheritdoc />
    public override string ToString() =>
        $"MtProtoTransportConfig {{ SourceId = {SourceId}, ApiId = {ApiId}, ApiHash = ***, " +
        $"SessionData = {(SessionData is null ? "null" : "***")}, " +
        $"Phone = {(Phone is null ? "null" : "***")}, Proxy = {Proxy} }}";
}

/// <summary>Новое сообщение чата, полученное транспортом MTProto.</summary>
/// <param name="MessageId">Идентификатор сообщения Telegram.</param>
/// <param name="Text">Текст сообщения; пустые сообщения отбрасываются адаптером.</param>
/// <param name="Author">Автор: username или id; <c>null</c>, если неизвестен.</param>
/// <param name="ChatTitle">Название чата.</param>
/// <param name="ChatUsername">Username чата; <c>null</c> у приватных чатов.</param>
/// <param name="PostedAt">Время публикации (UTC).</param>
public sealed record MtProtoIncomingMessage(
    long MessageId,
    string Text,
    string? Author,
    string ChatTitle,
    string? ChatUsername,
    DateTimeOffset PostedAt);

/// <summary>Вид отказа Telegram-транспорта.</summary>
public enum TelegramFailureKind
{
    /// <summary>Сетевая ошибка или недоступный прокси: повторить с backoff.</summary>
    Network,

    /// <summary>FloodWait: дождаться <see cref="TelegramTransportException.RetryAfter"/> и повторить.</summary>
    FloodWait,

    /// <summary>Сессия недействительна, аккаунт разлогинен или забанен: переподключения бессмысленны.</summary>
    Auth,

    /// <summary>Ошибка конфигурации (сессия/прокси/чат): нужна правка настроек, а не повтор.</summary>
    Configuration,
}

/// <summary>Типизированный отказ транспорта: адаптер превращает его в состояние health.</summary>
public sealed class TelegramTransportException : Exception
{
    /// <summary>Создаёт отказ транспорта.</summary>
    /// <param name="kind">Вид отказа.</param>
    /// <param name="message">Описание для health; не содержит секретов.</param>
    /// <param name="retryAfter">Срок ожидания FloodWait.</param>
    /// <param name="innerException">Причина.</param>
    public TelegramTransportException(
        TelegramFailureKind kind,
        string message,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        RetryAfter = retryAfter;
    }

    /// <summary>Вид отказа.</summary>
    public TelegramFailureKind Kind { get; }

    /// <summary>Срок ожидания для <see cref="TelegramFailureKind.FloodWait"/>.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// Транспорт WTelegramClient за внутренним интерфейсом: тесты подменяют его заглушкой
/// и не обращаются к реальной сети. Адаптер знает только этот контракт.
/// </summary>
public interface ITelegramClientTransport : IAsyncDisposable
{
    /// <summary>
    /// Подключается к Telegram, входит в аккаунт и разрешает чат источника. Текущее состояние
    /// чата считается прочитанным: транспорт отдаёт только сообщения, появившиеся после открытия.
    /// </summary>
    /// <param name="config">API id/hash, сессия и прокси.</param>
    /// <param name="chatId">Нормализованный chatId источника (@username или числовой id).</param>
    /// <param name="ct">Токен отмены.</param>
    Task OpenAsync(MtProtoTransportConfig config, string chatId, CancellationToken ct);

    /// <summary>Поток новых сообщений чата; завершается только по отмене.</summary>
    /// <param name="ct">Токен отмены.</param>
    IAsyncEnumerable<MtProtoIncomingMessage> ReadAsync(CancellationToken ct);
}

/// <summary>Фабрика транспортов MTProto: позволяет подменить WTelegramClient в тестах.</summary>
public interface ITelegramClientTransportFactory
{
    /// <summary>Создаёт новый транспорт; предыдущий должен быть освобождён адаптером.</summary>
    ITelegramClientTransport Create();
}

/// <summary>
/// Хранилище обновлённой сессии WTelegramClient. Сессия не пишется на диск:
/// реализация по умолчанию ничего не делает, а промышленная реализация сохранит
/// <c>sessionData</c> обратно в зашифрованный секрет.
/// </summary>
/// <remarks>
/// TODO: сохранять обновлённую сессию в <c>secrets</c> через <c>ISecretStore</c>, чтобы
/// после перезапуска Worker-а не требовался повторный вход в аккаунт. Пока сессия живёт в памяти
/// процесса и в исходном секрете.
/// </remarks>
public interface IMtProtoSessionStore
{
    /// <summary>Сохраняет сессию источника.</summary>
    /// <param name="sourceId">Источник.</param>
    /// <param name="sessionData">Новая сессия (Base64).</param>
    /// <param name="ct">Токен отмены.</param>
    Task SaveAsync(Guid sourceId, string sessionData, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Реализация <see cref="IMtProtoSessionStore"/> по умолчанию: сессия не сохраняется.</summary>
public sealed class NullMtProtoSessionStore : IMtProtoSessionStore
{
    /// <summary>Единственный экземпляр no-op хранилища.</summary>
    public static NullMtProtoSessionStore Instance { get; } = new();

    /// <inheritdoc />
    public Task SaveAsync(Guid sourceId, string sessionData, CancellationToken ct) =>
        Task.CompletedTask;
}
