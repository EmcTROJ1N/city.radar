namespace Sources.Contracts;

/// <summary>
/// Разрешение параметров подключения источника для Worker-а.
/// Прокси выбирается в порядке «источник → аккаунт Telegram → глобальная настройка
/// <c>proxy_secret_id</c>»; первый заданный побеждает. Если прокси задан хотя бы в одном звене,
/// <see cref="ResolvedConnection.ProxyRequired"/> = <c>true</c> и прямое соединение запрещено
/// (используется всегда). Регистрируется только вместе с <see cref="ISecretReader"/>
/// через <c>AddSecretReader()</c>.
/// </summary>
public interface ISourceConnectionResolver
{
    /// <summary>
    /// Возвращает сессию и прокси источника или <c>null</c>, если источника не существует.
    /// </summary>
    Task<ResolvedConnection?> ResolveAsync(Guid sourceId, CancellationToken ct);
}

/// <summary>
/// Параметры подключения источника: расшифрованная строка сессии (только для Worker-а), прокси
/// и признак обязательности прокси. Значения не должны попадать в ответы API или логи.
/// </summary>
/// <param name="SourceId">Идентификатор источника.</param>
/// <param name="SessionSecret">Строка Telegram-сессии или <c>null</c>, если сессия не назначена.</param>
/// <param name="Proxy">Действующий прокси или <c>null</c>, если прокси не задан ни в одном звене.</param>
/// <param name="ProxyRequired">
/// <c>true</c>, если прокси задан в источнике, аккаунте Telegram или глобальных настройках:
/// прямое соединение в этом случае запрещено.
/// </param>
public sealed record ResolvedConnection(
    Guid SourceId,
    string? SessionSecret,
    ProxyInfo? Proxy,
    bool ProxyRequired);

/// <summary>
/// Прокси (SOCKS5/MTProxy/HTTP) из секрета вида <c>proxy</c>. Используется Worker-ом;
/// <see cref="Url"/> содержит учётные данные и наружу не отдаётся.
/// </summary>
public sealed record ProxyInfo(
    string Scheme,
    string Host,
    int Port,
    string? Username,
    string? Password)
{
    /// <summary>URL прокси для адаптера: <c>scheme://[user:password@]host:port</c>.</summary>
    public string Url
    {
        get
        {
            var credentials = string.IsNullOrEmpty(Username)
                ? string.Empty
                : string.IsNullOrEmpty(Password)
                    ? $"{Uri.EscapeDataString(Username)}@"
                    : $"{Uri.EscapeDataString(Username)}:{Uri.EscapeDataString(Password)}@";

            return $"{Scheme}://{credentials}{Host}:{Port}";
        }
    }
}
