using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Ingestion.Abstractions;

/// <summary>
/// Построение HTTP-транспорта адаптеров. Если прокси задан, соединение
/// строится только через него: <see cref="SocketsHttpHandler.ConnectCallback"/> пропускает
/// подключение лишь к самому прокси, а недоступный прокси даёт исключение — прямой сокет
/// не открывается никогда.
/// </summary>
/// <remarks>
/// Bot API работает по HTTPS, поэтому поддерживаются SOCKS5 и HTTP-прокси (CONNECT). MTProxy —
/// протокол Telegram MTProto; для Bot API он неприменим и даёт понятную ошибку конфигурации
/// (MTProxy используется только адаптером MTProto).
/// </remarks>
public static class ProxyHttpHandlerFactory
{
    /// <summary>Схема SOCKS5-прокси.</summary>
    public const string Socks5Scheme = "socks5";

    /// <summary>Схема HTTP-прокси (CONNECT для HTTPS).</summary>
    public const string HttpScheme = "http";

    /// <summary>Схема MTProxy (только MTProto, для Bot API не применяется).</summary>
    public const string MtProxyScheme = "mtproxy";

    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Создаёт обработчик для <see cref="HttpClient"/>. Без прокси возвращает обычный обработчик
    /// (прямое соединение разрешено); с прокси — обработчик, который физически не умеет
    /// подключаться напрямую.
    /// </summary>
    /// <param name="proxy">Прокси или <c>null</c>, если прокси не задан.</param>
    /// <param name="proxyRequired">
    /// <c>true</c>, если прокси задан в настройках (источник/аккаунт/глобально): отсутствие
    /// параметров прокси в этом случае — ошибка конфигурации, а не разрешение на прямое соединение.
    /// </param>
    /// <param name="connectTimeout">Таймаут установки соединения; по умолчанию 20 секунд.</param>
    /// <exception cref="ProxyConfigurationException">Прокси обязателен, но не задан, или схема неприменима.</exception>
    public static SocketsHttpHandler Create(
        ProxySettings? proxy,
        bool proxyRequired = false,
        TimeSpan? connectTimeout = null)
    {
        if (proxy is null)
        {
            if (proxyRequired)
            {
                throw new ProxyConfigurationException(
                    "Прокси задан в настройках источника, но его параметры не разрешились: " +
                    "прямое соединение запрещено. Проверьте секрет прокси и привязку.");
            }

            // Прокси не настроен ни в одном звене — прямое соединение разрешено.
            return new SocketsHttpHandler();
        }

        return CreateProxyOnly(proxy, connectTimeout ?? DefaultConnectTimeout);
    }

    /// <summary>
    /// Создаёт обработчик, все соединения которого идут только через прокси. Любая попытка
    /// открыть сокет мимо прокси (например, при ошибке в настройках) прерывается исключением.
    /// </summary>
    /// <param name="proxy">Параметры прокси.</param>
    /// <param name="connectTimeout">Таймаут установки соединения с прокси.</param>
    /// <exception cref="ProxyConfigurationException">Схема прокси неизвестна или неприменима к HTTPS.</exception>
    public static SocketsHttpHandler CreateProxyOnly(ProxySettings proxy, TimeSpan connectTimeout)
    {
        ArgumentNullException.ThrowIfNull(proxy);

        var scheme = NormalizeScheme(proxy);
        if (string.Equals(scheme, MtProxyScheme, StringComparison.Ordinal))
        {
            throw new ProxyConfigurationException(
                "MTProxy не применяется к Telegram Bot API: Bot API работает по HTTPS. " +
                "Укажите SOCKS5 или HTTP-прокси (MTProxy поддерживается только адаптером MTProto).");
        }

        if (string.IsNullOrWhiteSpace(proxy.Host))
        {
            throw new ProxyConfigurationException("Не задан хост прокси.");
        }

        if (proxy.Port is <= 0 or > 65535)
        {
            throw new ProxyConfigurationException(
                $"Некорректный порт прокси: {proxy.Port.ToString(CultureInfo.InvariantCulture)}.");
        }

        var webProxy = new WebProxy(new Uri($"{scheme}://{proxy.Host}:{proxy.Port}"));
        if (!string.IsNullOrEmpty(proxy.Username))
        {
            webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password ?? string.Empty);
        }

        return new SocketsHttpHandler
        {
            Proxy = webProxy,
            UseProxy = true,
            ConnectTimeout = connectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = (context, ct) => ConnectThroughProxyAsync(context, proxy, ct),
        };
    }

    /// <summary>
    /// Проверяет достижимость прокси отдельным TCP-подключением (соединение только с прокси,
    /// не с целевым хостом). Используется адаптером MTProto перед созданием клиента.
    /// </summary>
    /// <param name="proxy">Параметры прокси.</param>
    /// <param name="timeout">Таймаут проверки.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <exception cref="ProxyUnavailableException">Прокси недоступен.</exception>
    public static async Task EnsureAvailableAsync(
        ProxySettings proxy,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proxy);

        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await socket.ConnectAsync(proxy.Host, proxy.Port, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProxyUnavailableException(
                $"Прокси {proxy.Host}:{proxy.Port} не ответил за отведённое время. " +
                "Прямое соединение запрещено.");
        }
        catch (SocketException exception)
        {
            throw new ProxyUnavailableException(
                $"Прокси {proxy.Host}:{proxy.Port} недоступен: {exception.SocketErrorCode}. " +
                "Прямое соединение запрещено.",
                exception);
        }
    }

    /// <summary>
    /// Проверяет, что соединение устанавливается с прокси, а не с целевым хостом. Публичный
    /// метод используется <see cref="SocketsHttpHandler.ConnectCallback"/> и тестами:
    /// при настроенном прокси прямое подключение запрещено.
    /// </summary>
    /// <param name="endpoint">Куда транспорт пытается открыть сокет.</param>
    /// <param name="proxy">Настроенный прокси.</param>
    /// <exception cref="DirectConnectionForbiddenException">Endpoint не совпадает с прокси.</exception>
    public static void EnsureEndpointAllowed(DnsEndPoint endpoint, ProxySettings proxy)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(proxy);

        if (!IsProxyEndpoint(endpoint, proxy))
        {
            throw new DirectConnectionForbiddenException(
                $"Прямое соединение с {endpoint.Host}:{endpoint.Port} запрещено: настроен прокси " +
                $"{proxy.Host}:{proxy.Port}. Соединение устанавливается только через прокси.");
        }
    }

    private static async ValueTask<Stream> ConnectThroughProxyAsync(
        SocketsHttpConnectionContext context,
        ProxySettings proxy,
        CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;

        // При настроенном прокси транспорт обязан подключаться только к прокси. Если сюда
        // пришёл целевой хост — значит, кто-то пытается обойти прокси: соединение запрещено.
        EnsureEndpointAllowed(endpoint, proxy);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            socket.Dispose();
            throw;
        }
        catch (SocketException exception)
        {
            socket.Dispose();

            // Прямого fallback нет и быть не может: недоступный прокси — это ошибка источника.
            throw new ProxyUnavailableException(
                $"Прокси {proxy.Host}:{proxy.Port} недоступен: {exception.SocketErrorCode}. " +
                "Прямое соединение запрещено.",
                exception);
        }

        return new NetworkStream(socket, ownsSocket: true);
    }

    private static bool IsProxyEndpoint(DnsEndPoint endpoint, ProxySettings proxy) =>
        endpoint.Port == proxy.Port &&
        string.Equals(endpoint.Host, proxy.Host, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeScheme(ProxySettings proxy)
    {
        var scheme = proxy.Scheme.Trim().ToLowerInvariant();

        return scheme switch
        {
            Socks5Scheme or "socks5h" or "socks" => Socks5Scheme,
            HttpScheme or "https" => HttpScheme,
            MtProxyScheme or "mtproxysecret" => MtProxyScheme,
            _ => throw new ProxyConfigurationException(
                $"Неизвестная схема прокси \"{proxy.Scheme}\": допустимы socks5, http и mtproxy " +
                "(mtproxy — только для MTProto)."),
        };
    }
}

/// <summary>
/// Разрешение действующего прокси. Цепочка «источник → аккаунт Telegram →
/// глобальная настройка» разрешается <c>ISourceConnectionResolver</c> в Worker-е, поэтому
/// <see cref="SourceRuntimeConfig.Proxy"/> уже содержит победившее звено; этот резолвер добавляет
/// последний уровень (глобальный прокси, если он не попал в снимок) и переиспользуется там,
/// где нужен явный порядок звеньев.
/// </summary>
public sealed class ProxyResolver
{
    /// <summary>
    /// Возвращает действующий прокси: прокси из снимка источника (источник → аккаунт) важнее
    /// глобального. <c>null</c> — прокси не задан ни в одном звене.
    /// </summary>
    /// <param name="config">Снимок источника; <see cref="SourceRuntimeConfig.Proxy"/> уже содержит
    /// результат разрешения «источник → аккаунт → глобальный» в Worker-е.</param>
    /// <param name="globalProxy">Глобальный прокси на случай, если он не попал в снимок.</param>
    public ProxySettings? Resolve(SourceRuntimeConfig config, ProxySettings? globalProxy)
    {
        ArgumentNullException.ThrowIfNull(config);

        return Resolve(config.Proxy, accountProxy: null, globalProxy);
    }

    /// <summary>
    /// Явный порядок цепочки: прокси источника → прокси аккаунта Telegram → глобальный прокси.
    /// Первый заданный побеждает.
    /// </summary>
    /// <param name="sourceProxy">Прокси источника.</param>
    /// <param name="accountProxy">Прокси аккаунта Telegram.</param>
    /// <param name="globalProxy">Глобальный прокси.</param>
    public static ProxySettings? Resolve(
        ProxySettings? sourceProxy,
        ProxySettings? accountProxy,
        ProxySettings? globalProxy) => sourceProxy ?? accountProxy ?? globalProxy;
}

/// <summary>Ошибка конфигурации прокси: параметры неполны или схема неприменима к транспорту.</summary>
public sealed class ProxyConfigurationException : InvalidOperationException
{
    /// <summary>Создаёт ошибку конфигурации прокси.</summary>
    /// <param name="message">Понятное администратору описание.</param>
    public ProxyConfigurationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Прокси недоступен. Соединение не подменяется прямым: адаптер переходит в состояние
/// <see cref="SourceState.Error"/>.
/// </summary>
public sealed class ProxyUnavailableException : Exception
{
    /// <summary>Создаёт ошибку недоступности прокси.</summary>
    /// <param name="message">Описание для health-детали.</param>
    /// <param name="innerException">Причина (например, <see cref="SocketException"/>).</param>
    public ProxyUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Попытка открыть сокет мимо настроенного прокси. Это нарушение: соединение прерывается
/// до открытия сокета, а не выполняется напрямую.
/// </summary>
public sealed class DirectConnectionForbiddenException : InvalidOperationException
{
    /// <summary>Создаёт ошибку запрещённого прямого соединения.</summary>
    /// <param name="message">Описание нарушения.</param>
    public DirectConnectionForbiddenException(string message)
        : base(message)
    {
    }
}
