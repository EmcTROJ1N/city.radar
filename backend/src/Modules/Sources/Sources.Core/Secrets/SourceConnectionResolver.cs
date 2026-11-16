using BuildingBlocks.Persistence;

using IdentityAccess.Contracts;

using Microsoft.EntityFrameworkCore;

using Sources.Contracts;
using Sources.Core.Domain;

namespace Sources.Core.Secrets;

/// <summary>
/// Порядок разрешения прокси: источник → аккаунт Telegram (секрет
/// сессии) → глобальная настройка <c>proxy_secret_id</c>. Первый заданный побеждает.
/// Если прокси задан хотя бы в одном звене, <see cref="ResolvedConnection.ProxyRequired"/>
/// = <c>true</c>: Worker не имеет права подключаться напрямую.
/// </summary>
internal sealed class SourceConnectionResolver(
    MayakDbContext db,
    SecretReader reader,
    IGlobalSettings globalSettings) : ISourceConnectionResolver
{
    public async Task<ResolvedConnection?> ResolveAsync(Guid sourceId, CancellationToken ct)
    {
        var source = await db.Set<Source>()
            .AsNoTracking()
            .Where(item => item.Id == sourceId)
            .Select(item => new
            {
                item.Id,
                item.TelegramSessionId,
                item.ProxyId,
            })
            .FirstOrDefaultAsync(ct);
        if (source is null)
        {
            return null;
        }

        string? sessionSecret = null;
        Guid? accountProxyId = null;

        if (source.TelegramSessionId is { } sessionSecretId)
        {
            sessionSecret = await reader.ReadAsync(sessionSecretId, ct);
            accountProxyId = await reader.ReadProxyIdAsync(sessionSecretId, ct);
        }

        var settings = await globalSettings.GetAsync(ct);

        // Прокси задан в любом звене — прямое соединение запрещено, даже если ссылка
        // не разрешается в существующий секрет (лучше не подключаться, чем подключиться напрямую).
        var proxyRequired = source.ProxyId is not null
            || accountProxyId is not null
            || settings.ProxySecretId is not null;

        var effectiveProxyId = source.ProxyId ?? accountProxyId ?? settings.ProxySecretId;
        var proxy = effectiveProxyId is { } proxyId
            ? await reader.ReadProxyAsync(proxyId, ct)
            : null;

        return new ResolvedConnection(source.Id, sessionSecret, proxy, proxyRequired);
    }
}
