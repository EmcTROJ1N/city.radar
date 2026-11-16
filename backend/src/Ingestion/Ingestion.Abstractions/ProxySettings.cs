namespace Ingestion.Abstractions;

/// <summary>
/// Параметры прокси. Если прокси задан, соединение строится только через него:
/// прямое подключение запрещено.
/// </summary>
/// <param name="Scheme">Схема подключения: <c>socks5</c>, <c>mtproxy</c> или <c>http</c>.</param>
/// <param name="Host">Хост прокси.</param>
/// <param name="Port">Порт прокси.</param>
/// <param name="Username">Имя пользователя; <c>null</c>, если авторизация не требуется.</param>
/// <param name="Password">Пароль; <c>null</c>, если авторизация не требуется. В логи не выводится.</param>
public sealed record ProxySettings(
    string Scheme,
    string Host,
    int Port,
    string? Username = null,
    string? Password = null)
{
    /// <summary>
    /// Представление для логов и сообщений об ошибках: пароль заменяется на <c>***</c>,
    /// чтобы секрет не попал в журнал.
    /// </summary>
    public override string ToString()
    {
        var username = Username is null ? string.Empty : $", Username = {Username}";
        var password = Password is null ? string.Empty : ", Password = ***";

        return $"ProxySettings {{ Scheme = {Scheme}, Host = {Host}, Port = {Port}{username}{password} }}";
    }
}
