using System.Text;
using System.Text.Json;

using BuildingBlocks.Persistence;
using BuildingBlocks.Security;
using BuildingBlocks.Web;

using IdentityAccess.Contracts;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using Npgsql;

using Sources.Core.Domain;

namespace Sources.Core.Secrets;

/// <summary>
/// Запись секретов. Значение шифруется
/// <see cref="ISecretCipher"/> и сохраняется только как <c>ciphertext</c> с версией ключа;
/// наружу возвращаются лишь метаданные. Каждое изменение пишет аудит без значений и
/// сохраняется одним <c>SaveChanges</c>. Метод <c>PUT /admin/secrets/llm</c> заменяет
/// единственный секрет LLM (частичный уникальный индекс <c>secrets_single_llm</c>).
/// </summary>
internal sealed class SecretStore(
    MayakDbContext db,
    ISecretCipher cipher,
    IAuditLog audit,
    ICurrentUser currentUser,
    TimeProvider time,
    SecretValidationService validator,
    IConfiguration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string EntityType = "secret";
    private const string LlmSecretName = "LLM";

    /// <summary>
    /// Проверять ли секреты перед сохранением. По умолчанию включено; тестовый хост
    /// отключает проверку настройкой <c>Secrets:ValidateOnSave=false</c>, чтобы не ходить
    /// в реальные Telegram/LLM.
    /// </summary>
    private bool ValidateOnSave =>
        !string.Equals(
            configuration["Secrets:ValidateOnSave"],
            "false",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Список секретов: вид, название, наличие значения, прокси и время изменения.</summary>
    public async Task<IResult> ListAsync(CancellationToken ct)
    {
        var secrets = await db.Set<Secret>()
            .AsNoTracking()
            .OrderBy(secret => secret.Kind)
            .ThenBy(secret => secret.Name)
            .ThenBy(secret => secret.Id)
            .Select(secret => new SecretMetaResponse(
                secret.Id,
                secret.Kind,
                secret.Name,
                secret.ProxyId,
                true,
                secret.UpdatedAt))
            .ToListAsync(ct);

        return Results.Ok(secrets);
    }

    /// <summary>Создаёт или заменяет Telegram-сессию по идентификатору из пути.</summary>
    public async Task<IResult> PutTelegramSessionAsync(
        Guid id,
        TelegramSessionInput input,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var proxyError = await ValidateProxyReferenceAsync(input.ProxyId, ct);
        if (proxyError is not null)
        {
            return ProblemResponses.Validation(proxyError);
        }

        if (ValidateOnSave)
        {
            var validationError = await validator
                .ValidateTelegramSessionAsync(input.Session, input.ProxyId, ct)
                .ConfigureAwait(false);

            if (validationError is not null)
            {
                return ProblemResponses.Validation(validationError);
            }
        }

        return await SaveSecretAsync(
            id,
            SecretKinds.TelegramSession,
            input.Name,
            Encoding.UTF8.GetBytes(input.Session),
            input.ProxyId,
            ct);
    }

    /// <summary>Создаёт или заменяет прокси по идентификатору из пути.</summary>
    public async Task<IResult> PutProxyAsync(Guid id, ProxyInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (ValidateOnSave)
        {
            var validationError = await validator
                .ValidateProxyAsync(input.Host, input.Port, ct)
                .ConfigureAwait(false);

            if (validationError is not null)
            {
                return ProblemResponses.Validation(validationError);
            }
        }

        var payload = new ProxyPayload(
            input.Scheme,
            input.Host,
            input.Port,
            input.Username,
            input.Password);

        return await SaveSecretAsync(
            id,
            SecretKinds.Proxy,
            input.Name,
            JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions),
            proxyId: null,
            ct);
    }

    /// <summary>
    /// Задаёт или заменяет единственный секрет LLM. Если он уже существует, обновляется та же
    /// запись: второй ключ заменяет первый, а не создаёт дубль.
    /// </summary>
    public async Task<IResult> PutLlmAsync(LlmInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var proxyError = await ValidateProxyReferenceAsync(input.ProxyId, ct);
        if (proxyError is not null)
        {
            return ProblemResponses.Validation(proxyError);
        }

        if (ValidateOnSave)
        {
            var validationError = await validator
                .ValidateLlmAsync(input.ApiKey, input.BaseUrl, input.Model, input.ProxyId, ct)
                .ConfigureAwait(false);

            if (validationError is not null)
            {
                return ProblemResponses.Validation(validationError);
            }
        }

        var existing = await db.Set<Secret>()
            .FirstOrDefaultAsync(secret => secret.Kind == SecretKinds.Llm, ct);

        var payload = new LlmPayload(input.ApiKey, input.BaseUrl, input.Model);

        return await SaveSecretAsync(
            existing?.Id ?? Guid.NewGuid(),
            SecretKinds.Llm,
            existing?.Name ?? LlmSecretName,
            JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions),
            input.ProxyId,
            ct);
    }

    /// <summary>
    /// Шифрует значение и сохраняет секрет вместе с записью аудита. В аудит попадают только
    /// вид, название, версия ключа и признак замены — ни значение, ни шифртекст.
    /// </summary>
    private async Task<IResult> SaveSecretAsync(
        Guid id,
        string kind,
        string name,
        byte[] plaintext,
        Guid? proxyId,
        CancellationToken ct)
    {
        var secret = await db.Set<Secret>()
            .FirstOrDefaultAsync(item => item.Id == id, ct);

        if (secret is not null && !string.Equals(secret.Kind, kind, StringComparison.Ordinal))
        {
            return ProblemResponses.Validation(
                new ValidationError("id", "Секрет с этим идентификатором имеет другой вид"));
        }

        var replaced = secret is not null;
        var keyVersion = cipher.CurrentKeyVersion;
        var now = time.GetUtcNow();

        var before = secret is null
            ? null
            : new SecretAuditView(secret.Kind, secret.Name, secret.KeyVersion, Replaced: true);

        if (secret is null)
        {
            secret = new Secret
            {
                Id = id,
                Kind = kind,
                CreatedBy = currentUser.UserId,
                CreatedAt = now,
            };
            db.Add(secret);
        }

        secret.Name = name;
        secret.Ciphertext = cipher.Encrypt(plaintext, keyVersion);
        secret.KeyVersion = keyVersion;
        secret.ProxyId = proxyId;
        secret.UpdatedAt = now;

        await audit.WriteAsync(
            replaced ? "secret.replace" : "secret.create",
            EntityType,
            id.ToString(),
            before,
            new SecretAuditView(kind, name, keyVersion, replaced),
            ct: ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
            })
        {
            return ProblemResponses.Conflict(
                $"Секрет вида {kind} с названием «{name}» уже существует");
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.ForeignKeyViolation,
            })
        {
            return ProblemResponses.Conflict("Секрет ссылается на несуществующую запись");
        }

        return Results.NoContent();
    }

    /// <summary>Проверяет, что <paramref name="proxyId"/> указывает на секрет вида <c>proxy</c>.</summary>
    private async Task<ValidationError?> ValidateProxyReferenceAsync(
        Guid? proxyId,
        CancellationToken ct)
    {
        if (proxyId is not { } id)
        {
            return null;
        }

        var kind = await db.Set<Secret>()
            .AsNoTracking()
            .Where(secret => secret.Id == id)
            .Select(secret => secret.Kind)
            .FirstOrDefaultAsync(ct);

        return kind switch
        {
            null => new ValidationError("proxyId", "Прокси не найден"),
            SecretKinds.Proxy => null,
            _ => new ValidationError("proxyId", "Секрет должен быть прокси"),
        };
    }
}

/// <summary>Элемент ответа <c>GET /admin/secrets</c> (схема <c>SecretMeta</c>): без значения.</summary>
internal sealed record SecretMetaResponse(
    Guid Id,
    string Kind,
    string Name,
    Guid? ProxyId,
    bool HasValue,
    DateTimeOffset UpdatedAt);

/// <summary>Метаданные изменения секрета для аудита; значения и шифртекст сюда не попадают.</summary>
internal sealed record SecretAuditView(
    string Kind,
    string Name,
    int KeyVersion,
    bool Replaced);
