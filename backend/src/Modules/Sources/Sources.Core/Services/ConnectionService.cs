using System.Text.Json;

using BuildingBlocks.Persistence;
using BuildingBlocks.Web;
using IdentityAccess.Contracts;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

using Npgsql;

using Sources.Core.Domain;
using Sources.Core.Secrets;

namespace Sources.Core.Services;

/// <summary>
/// Подключение источника: какие Telegram-сессия и прокси назначены
/// источнику. Метод возвращает только идентификаторы секретов, значения не отдаются.
/// Сессия допустима только для источника типа <c>telegram_chat</c>; каждая ссылка должна
/// указывать на секрет нужного вида. Изменение пишется в аудит и сохраняется одним
/// <c>SaveChanges</c>.
/// </summary>
internal sealed class ConnectionService(MayakDbContext db, IAuditLog audit, SecretStore secrets)
{
    private const string EntityType = "source";
    private const string TelegramSourceType = "telegram_chat";
    private const string TelegramBotMode = "bot";

    public async Task<IResult> GetAsync(Guid sourceId, CancellationToken ct)
    {
        var source = await FindSourceAsync(sourceId, ct, tracking: false);
        if (source is null)
        {
            return ProblemResponses.NotFound($"Источник {sourceId} не существует");
        }

        return Results.Ok(new SourceConnectionResponse(source.TelegramSessionId, source.ProxyId));
    }

    public async Task<IResult> UpdateAsync(
        Guid sourceId,
        ConnectionInput input,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var source = await FindSourceAsync(sourceId, ct, tracking: true);
        if (source is null)
        {
            return ProblemResponses.NotFound($"Источник {sourceId} не существует");
        }

        var errors = new List<ValidationError>();

        if (input.TelegramSessionId is { } sessionSecretId)
        {
            if (!string.Equals(source.SourceType, TelegramSourceType, StringComparison.Ordinal))
            {
                errors.Add(new ValidationError(
                    "telegramSessionId",
                    "Telegram-сессия назначается только источнику типа telegram_chat"));
            }
            else if (!await IsSecretOfKindAsync(sessionSecretId, SecretKinds.TelegramSession, ct))
            {
                errors.Add(new ValidationError(
                    "telegramSessionId",
                    "Секрет должен быть Telegram-сессией"));
            }
            else
            {
                var secretMode = await secrets.GetTelegramModeAsync(sessionSecretId, ct);
                var sourceMode = ReadMode(source.Settings);

                if (secretMode is not null
                    && !string.Equals(secretMode, sourceMode, StringComparison.Ordinal))
                {
                    errors.Add(new ValidationError(
                        "telegramSessionId",
                        "Способ входа секрета не совпадает со способом чтения источника. "
                        + "Для MTProto выберите источник с режимом userbot и MTProto-сессию."));
                }
            }
        }

        if (input.ProxyId is { } proxySecretId
            && !await IsSecretOfKindAsync(proxySecretId, SecretKinds.Proxy, ct))
        {
            errors.Add(new ValidationError("proxyId", "Секрет должен быть прокси"));
        }

        if (errors.Count > 0)
        {
            return ProblemResponses.Validation([.. errors]);
        }

        var before = new SourceConnectionAuditView(source.TelegramSessionId, source.ProxyId);

        source.TelegramSessionId = input.TelegramSessionId;
        source.ProxyId = input.ProxyId;

        await audit.WriteAsync(
            "source.connection.update",
            EntityType,
            source.Id.ToString(),
            before,
            new SourceConnectionAuditView(source.TelegramSessionId, source.ProxyId),
            ct: ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.ForeignKeyViolation,
            })
        {
            return ProblemResponses.Conflict("Ссылка на несуществующий секрет");
        }

        return Results.Ok(new SourceConnectionResponse(source.TelegramSessionId, source.ProxyId));
    }

    private Task<Source?> FindSourceAsync(Guid sourceId, CancellationToken ct, bool tracking)
    {
        var query = tracking
            ? db.Set<Source>()
            : db.Set<Source>().AsNoTracking();

        return query.FirstOrDefaultAsync(source => source.Id == sourceId, ct);
    }

    private Task<bool> IsSecretOfKindAsync(Guid secretId, string kind, CancellationToken ct) =>
        db.Set<Secret>()
            .AsNoTracking()
            .AnyAsync(secret => secret.Id == secretId && secret.Kind == kind, ct);

    /// <summary>
    /// Способ чтения источника из <c>settings.mode</c>; отсутствие режима у Telegram
    /// исторически означало юзербот, но API всегда писал <c>bot</c>, поэтому для UI
    /// и проверки связки по умолчанию считаем <c>bot</c>.
    /// </summary>
    private static string ReadMode(string settings)
    {
        try
        {
            using var document = JsonDocument.Parse(settings);

            if (document.RootElement.TryGetProperty("mode", out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? TelegramBotMode;
            }
        }
        catch (JsonException)
        {
            // Некорректный settings — считаем режим Bot API.
        }

        return TelegramBotMode;
    }
}

/// <summary>Ответ <c>SourceConnection</c>: идентификаторы секретов, без значений.</summary>
internal sealed record SourceConnectionResponse(Guid? TelegramSessionId, Guid? ProxyId);

/// <summary>Снимок привязок для аудита: только идентификаторы секретов.</summary>
internal sealed record SourceConnectionAuditView(Guid? TelegramSessionId, Guid? ProxyId);
