using System.Text;
using System.Text.Json;

using BuildingBlocks.Persistence;
using BuildingBlocks.Security;

using Microsoft.EntityFrameworkCore;

using Sources.Contracts;
using Sources.Core.Domain;

namespace Sources.Core.Secrets;

/// <summary>
/// Расшифровка секретов для Worker-а. Живёт только
/// в процессе обработки: в <c>Api.Host</c> <see cref="ISecretReader"/> не регистрируется,
/// поэтому API физически не умеет прочитать значение. Значения не логируются.
/// </summary>
internal sealed class SecretReader(MayakDbContext db, ISecretCipher cipher) : ISecretReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<string?> ReadAsync(Guid secretId, CancellationToken ct)
    {
        var secret = await db.Set<Secret>()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == secretId, ct);

        return secret is null ? null : Decrypt(secret);
    }

    /// <inheritdoc />
    public async Task<LlmSettings?> ReadLlmAsync(CancellationToken ct)
    {
        var secret = await db.Set<Secret>()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Kind == SecretKinds.Llm, ct);
        if (secret is null)
        {
            return null;
        }

        var payload = JsonSerializer.Deserialize<LlmPayload>(Decrypt(secret), JsonOptions)
            ?? throw new InvalidOperationException("Секрет LLM содержит некорректный JSON.");

        var proxyUrl = secret.ProxyId is { } proxyId
            ? (await ReadProxyAsync(proxyId, ct))?.Url
            : null;

        return new LlmSettings(payload.ApiKey, payload.BaseUrl, payload.Model, proxyUrl);
    }

    /// <summary>Прокси по идентификатору секрета; <c>null</c>, если секрет не найден или не прокси.</summary>
    internal async Task<ProxyInfo?> ReadProxyAsync(Guid proxyId, CancellationToken ct)
    {
        var secret = await db.Set<Secret>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.Id == proxyId && item.Kind == SecretKinds.Proxy,
                ct);
        if (secret is null)
        {
            return null;
        }

        var payload = JsonSerializer.Deserialize<ProxyPayload>(Decrypt(secret), JsonOptions)
            ?? throw new InvalidOperationException("Секрет прокси содержит некорректный JSON.");

        return new ProxyInfo(
            payload.Scheme,
            payload.Host,
            payload.Port,
            payload.Username,
            payload.Password);
    }

    /// <summary>Прокси, назначенный аккаунту Telegram (секрету сессии); <c>null</c>, если не задан.</summary>
    internal Task<Guid?> ReadProxyIdAsync(Guid secretId, CancellationToken ct) =>
        db.Set<Secret>()
            .AsNoTracking()
            .Where(item => item.Id == secretId)
            .Select(item => item.ProxyId)
            .FirstOrDefaultAsync(ct);

    private string Decrypt(Secret secret) =>
        Encoding.UTF8.GetString(cipher.Decrypt(secret.Ciphertext, secret.KeyVersion));
}

/// <summary>Вид секрета: значения колонки <c>secrets.kind</c> (check в БД и контракт).</summary>
internal static class SecretKinds
{
    public const string TelegramSession = "telegram_session";
    public const string Proxy = "proxy";
    public const string Llm = "llm";
}

/// <summary>Открытая структура секрета прокси до шифрования; в БД попадает только шифртекст.</summary>
internal sealed record ProxyPayload(
    string Scheme,
    string Host,
    int Port,
    string? Username,
    string? Password);

/// <summary>Открытая структура секрета LLM: хранится одним зашифрованным JSON.</summary>
internal sealed record LlmPayload(string ApiKey, string BaseUrl, string Model);
