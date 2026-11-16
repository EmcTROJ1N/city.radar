using System.Net.Sockets;
using System.Text;

using Ingestion.Abstractions;

namespace Ingestion.Adapters.TelegramMtProto;

/// <summary>
/// TCP-туннель через SOCKS5 или HTTP CONNECT для WTelegramClient.
/// При заданном прокси прямое TCP-соединение с Telegram не открывается: сначала устанавливается
/// соединение с прокси, затем внутри него — туннель до узла Telegram.
/// </summary>
internal static class ProxyTcpConnector
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Создаёт обработчик сокетов WTelegramClient, работающий только через прокси.</summary>
    /// <param name="proxy">Параметры прокси.</param>
    public static WTelegram.Client.TcpFactory Create(ProxySettings proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);

        var scheme = proxy.Scheme.Trim().ToLowerInvariant();

        return scheme switch
        {
            "socks5" or "socks5h" or "socks" => (host, port) => ConnectSocks5Async(proxy, host, port),
            "http" or "https" => (host, port) => ConnectHttpAsync(proxy, host, port),
            _ => throw new ProxyConfigurationException(
                $"Схема прокси \"{proxy.Scheme}\" не поддерживается MTProto-транспортом: " +
                "допустимы socks5, http и mtproxy."),
        };
    }

    private static async Task<TcpClient> ConnectSocks5Async(
        ProxySettings proxy,
        string host,
        int port)
    {
        var socket = await ConnectToProxyAsync(proxy).ConfigureAwait(false);

        try
        {
            var withCredentials = !string.IsNullOrEmpty(proxy.Username);

            // Приветствие: версия 5 и поддерживаемые методы аутентификации.
            await SendAsync(
                    socket,
                    withCredentials ? [0x05, 0x02, 0x00, 0x02] : [0x05, 0x01, 0x00])
                .ConfigureAwait(false);

            var greeting = await ReceiveExactlyAsync(socket, 2).ConfigureAwait(false);
            if (greeting[0] != 0x05)
            {
                throw new ProxyConfigurationException("SOCKS5-прокси ответил некорректным приветствием.");
            }

            switch (greeting[1])
            {
                case 0x00:
                    break;
                case 0x02 when withCredentials:
                    await AuthenticateSocks5Async(socket, proxy).ConfigureAwait(false);
                    break;
                case 0xFF:
                    throw new ProxyConfigurationException(
                        "SOCKS5-прокси не принял предложенные методы аутентификации.");
                default:
                    throw new ProxyConfigurationException(
                        $"SOCKS5-прокси требует неподдерживаемый метод аутентификации {greeting[1]}.");
            }

            // Запрос CONNECT: доменное имя, затем порт.
            var hostBytes = Encoding.UTF8.GetBytes(host);
            if (hostBytes.Length > 255)
            {
                throw new ProxyConfigurationException("Слишком длинное имя узла для SOCKS5-прокси.");
            }

            var request = new byte[7 + hostBytes.Length];
            request[0] = 0x05;
            request[1] = 0x01;
            request[2] = 0x00;
            request[3] = 0x03;
            request[4] = (byte)hostBytes.Length;
            hostBytes.CopyTo(request, 5);
            request[^2] = (byte)(port >> 8);
            request[^1] = (byte)(port & 0xFF);

            await SendAsync(socket, request).ConfigureAwait(false);

            var reply = await ReceiveExactlyAsync(socket, 4).ConfigureAwait(false);
            if (reply[0] != 0x05 || reply[1] != 0x00)
            {
                throw new ProxyConfigurationException(
                    $"SOCKS5-прокси отказал в соединении с {host}:{port} (код {reply[1]}).");
            }

            // Вычитываем адрес, который вернул прокси, чтобы поток не содержал лишних байт.
            var addressType = reply[3];
            var addressLength = addressType switch
            {
                0x01 => 4,
                0x04 => 16,
                0x03 => (await ReceiveExactlyAsync(socket, 1).ConfigureAwait(false))[0],
                _ => throw new ProxyConfigurationException(
                    $"SOCKS5-прокси вернул неизвестный тип адреса {addressType}."),
            };

            await ReceiveExactlyAsync(socket, addressLength + 2).ConfigureAwait(false);

            return Wrap(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task AuthenticateSocks5Async(
        Socket socket,
        ProxySettings proxy)
    {
        var username = Encoding.UTF8.GetBytes(proxy.Username ?? string.Empty);
        var password = Encoding.UTF8.GetBytes(proxy.Password ?? string.Empty);

        if (username.Length > 255 || password.Length > 255)
        {
            throw new ProxyConfigurationException("Логин или пароль SOCKS5-прокси длиннее 255 байт.");
        }

        var request = new byte[3 + username.Length + password.Length];
        request[0] = 0x01;
        request[1] = (byte)username.Length;
        username.CopyTo(request, 2);
        request[2 + username.Length] = (byte)password.Length;
        password.CopyTo(request, 3 + username.Length);

        await SendAsync(socket, request).ConfigureAwait(false);

        var reply = await ReceiveExactlyAsync(socket, 2).ConfigureAwait(false);
        if (reply[1] != 0x00)
        {
            throw new ProxyConfigurationException("SOCKS5-прокси отклонил логин или пароль.");
        }
    }

    private static async Task<TcpClient> ConnectHttpAsync(
        ProxySettings proxy,
        string host,
        int port)
    {
        var socket = await ConnectToProxyAsync(proxy).ConfigureAwait(false);

        try
        {
            var request = new StringBuilder()
                .Append("CONNECT ").Append(host).Append(':').Append(port).Append(" HTTP/1.1\r\n")
                .Append("Host: ").Append(host).Append(':').Append(port).Append("\r\n");

            if (!string.IsNullOrEmpty(proxy.Username))
            {
                var credentials = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{proxy.Username}:{proxy.Password ?? string.Empty}"));
                request.Append("Proxy-Authorization: Basic ").Append(credentials).Append("\r\n");
            }

            request.Append("\r\n");

            await SendAsync(socket, Encoding.ASCII.GetBytes(request.ToString())).ConfigureAwait(false);

            var response = await ReadHttpHeadersAsync(socket).ConfigureAwait(false);
            var statusLine = response.Split("\r\n", 2, StringSplitOptions.None)[0];

            if (!statusLine.Contains(" 200", StringComparison.Ordinal))
            {
                throw new ProxyConfigurationException(
                    $"HTTP-прокси отказал в CONNECT {host}:{port}: {statusLine}");
            }

            return Wrap(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<Socket> ConnectToProxyAsync(ProxySettings proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy.Host) || proxy.Port is <= 0 or > 65535)
        {
            throw new ProxyConfigurationException(
                $"Некорректные параметры прокси {proxy.Host}:{proxy.Port}.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var timeout = new CancellationTokenSource(ConnectTimeout);

        try
        {
            await socket.ConnectAsync(proxy.Host, proxy.Port, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            socket.Dispose();

            throw new ProxyUnavailableException(
                $"Прокси {proxy.Host}:{proxy.Port} не ответил за отведённое время. " +
                "Прямое соединение запрещено.");
        }
        catch (SocketException exception)
        {
            socket.Dispose();

            throw new ProxyUnavailableException(
                $"Прокси {proxy.Host}:{proxy.Port} недоступен: {exception.SocketErrorCode}. " +
                "Прямое соединение запрещено.",
                exception);
        }

        return socket;
    }

    private static async Task SendAsync(Socket socket, byte[] data)
    {
        var offset = 0;

        while (offset < data.Length)
        {
            var sent = await socket
                .SendAsync(data.AsMemory(offset), SocketFlags.None)
                .ConfigureAwait(false);

            if (sent == 0)
            {
                throw new IOException("Прокси закрыл соединение при отправке данных.");
            }

            offset += sent;
        }
    }

    private static async Task<byte[]> ReceiveExactlyAsync(Socket socket, int count)
    {
        var buffer = new byte[count];
        var offset = 0;

        while (offset < count)
        {
            var received = await socket
                .ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None)
                .ConfigureAwait(false);

            if (received == 0)
            {
                throw new IOException("Прокси закрыл соединение до завершения ответа.");
            }

            offset += received;
        }

        return buffer;
    }

    private static async Task<string> ReadHttpHeadersAsync(Socket socket)
    {
        var builder = new StringBuilder();
        var buffer = new byte[1];

        while (true)
        {
            var received = await socket
                .ReceiveAsync(buffer.AsMemory(), SocketFlags.None)
                .ConfigureAwait(false);

            if (received == 0)
            {
                throw new IOException("HTTP-прокси закрыл соединение до завершения заголовков.");
            }

            builder.Append((char)buffer[0]);

            if (builder.Length >= 4 &&
                builder[^1] == '\n' &&
                builder[^2] == '\r' &&
                builder[^3] == '\n' &&
                builder[^4] == '\r')
            {
                return builder.ToString();
            }
        }
    }

    private static TcpClient Wrap(Socket socket)
    {
        var client = new TcpClient { Client = socket, NoDelay = true };

        return client;
    }
}
