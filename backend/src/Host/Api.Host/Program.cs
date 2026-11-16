using Api.Host.Deployment;
using Api.Host.Seeding;

using BuildingBlocks.Messaging;
using BuildingBlocks.Persistence;
using BuildingBlocks.Security;
using BuildingBlocks.Web;

using Catalog.Core;
using Catalog.Core.Endpoints;

using IdentityAccess.Contracts;
using IdentityAccess.Core;
using IdentityAccess.Core.Admin;
using IdentityAccess.Core.Auth;
using IdentityAccess.Core.Seeding;

using MapReadModel.Core;
using MapReadModel.Core.Endpoints;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

using Moderation.Core;
using Moderation.Core.Endpoints;

using Observations.Infrastructure;

using Sources.Core;
using Sources.Core.Endpoints;
using Sources.Core.Secrets;

// Шаг деплоя «только миграции»: применяет миграции и роли и завершает
// процесс до создания веб-хоста. В Production Api миграции при старте не запускает — схему
// готовит отдельный сервис migrate (deploy/docker-compose.prod.yml).
if (MigrateOnlyRunner.IsRequested(args))
{
    Environment.ExitCode = await MigrateOnlyRunner.RunAsync(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);

const string ApiPrefix = "/api/v1";
const string PermissionClaimType = "permission";

// Redis-кэш и rate limit: без ConnectionStrings:Redis работают реализации в памяти.
builder.Services.AddRedisInfrastructure(builder.Configuration);

// БД нужна не всем сценариям (например, /health): без строки подключения приложение
// поднимается, а persistence/identity/авторизация просто не регистрируются.
var connectionString = builder.Configuration.GetConnectionString("Mayak");
var hasDatabase = !string.IsNullOrWhiteSpace(connectionString);

if (hasDatabase)
{
    builder.Services.AddPersistence(connectionString!);
    builder.Services.AddSecretCipher(builder.Environment);
    builder.Services.AddIdentityAccess();
    builder.Services.AddCatalog();
    builder.Services.AddSources();
    builder.Services.AddObservations();
    builder.Services.AddModeration();
    builder.Services.AddMapReadModel();
    builder.Services.AddOutbox();

    // Доставка сообщений Worker → Api. В тестах фон выключается
    // (Outbox:ApiDispatcherEnabled=false), singleton диспетчера остаётся в DI, и тесты
    // вызывают DispatchOnceAsync вручную — без гонки с фоновым циклом.
    var apiDispatcherEnabled = builder.Configuration.GetValue(
        "Outbox:ApiDispatcherEnabled",
        defaultValue: true);
    builder.Services.AddOutboxDispatcher("api", runInBackground: apiDispatcherEnabled);

    builder.Services.AddScoped<DevCatalogSeeder>();

    // Ключ подписи: в Production отсутствие Jwt:SigningKey — ошибка старта.
    var jwtOptions = JwtOptions.Load(builder.Configuration, builder.Environment);
    builder.Services.AddIdentityAccessAuth(jwtOptions);

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            // Claim'ы остаются как в токене: sub/name/permission без маппинга на ClaimTypes.*.
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwtOptions.Issuer,
                ValidateAudience = true,
                ValidAudience = jwtOptions.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = jwtOptions.CreateSigningKey(),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = "name",
            };
            options.Events = new JwtBearerEvents
            {
                OnChallenge = async context =>
                {
                    context.HandleResponse();
                    await ProblemResponses.WriteAsync(
                        context.Response,
                        StatusCodes.Status401Unauthorized,
                        "Не авторизован",
                        "Войдите, чтобы выполнить действие",
                        context.HttpContext.RequestAborted);
                },
                OnForbidden = async context =>
                {
                    await ProblemResponses.WriteAsync(
                        context.Response,
                        StatusCodes.Status403Forbidden,
                        "Доступ запрещён",
                        "Недостаточно прав",
                        context.HttpContext.RequestAborted);
                },
            };
        });

    // Одна политика на каждое право из матрицы: проверка только по permission-claim.
    builder.Services.AddAuthorization(options =>
    {
        foreach (var permission in Permissions.All)
        {
            options.AddPolicy(
                permission,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireClaim(PermissionClaimType, permission));
        }
    });
}

var app = builder.Build();

// Общий обработчик ошибок: любое необработанное исключение — Problem Details (RFC 7807).
app.UseExceptionHandler(errorApp => errorApp.Run(HandleExceptionAsync));

// Ответы без тела, которые формирует маршрутизация/биндинг (404 для несуществующего
// маршрута или не-GUID в {id:guid}, 405, 400 при неверном query), тоже приводим
// к application/problem+json: контракт описывает все ошибки по RFC 7807.
app.UseStatusCodePages(async statusCodeContext =>
{
    var request = statusCodeContext.HttpContext.Request;
    var response = statusCodeContext.HttpContext.Response;

    // TRACE и HEAD не имеют тела ответа: запись тела ломает HTTP-протокол (Kestrel).
    if (HttpMethods.IsTrace(request.Method) || HttpMethods.IsHead(request.Method))
    {
        return;
    }

    // RFC 9110: 405 обязан содержать Allow; ProblemResponses.Clear() сбрасывает заголовки,
    // поэтому возвращаем сохранённый список методов в OnStarting — до отправки заголовков.
    var allow = response.Headers.Allow.ToString();
    if (!string.IsNullOrEmpty(allow))
    {
        response.OnStarting(() =>
        {
            response.Headers.Allow = allow;

            return Task.CompletedTask;
        });
    }

    var (title, detail) = response.StatusCode switch
    {
        StatusCodes.Status400BadRequest =>
            ("Некорректный запрос", "Запрос не может быть обработан"),
        StatusCodes.Status404NotFound =>
            ("Не найдено", "Запрошенный ресурс не существует"),
        StatusCodes.Status405MethodNotAllowed =>
            ("Метод не разрешён", "Метод не поддерживается для этого ресурса"),
        _ => ("Ошибка запроса", "Запрос не может быть обработан"),
    };

    await ProblemResponses.WriteAsync(
        response,
        response.StatusCode,
        title,
        detail,
        statusCodeContext.HttpContext.RequestAborted);
});

// Строки запроса попадают в SQL-параметры (поиск, фильтры) и в jsonb; нулевой символ
// PostgreSQL не принимает, поэтому такой запрос — 400, а не 500 из недр БД.
app.Use(async (context, next) =>
{
    foreach (var value in context.Request.Query.SelectMany(parameter => parameter.Value))
    {
        if (!TextValidator.IsStorable(value))
        {
            await ProblemResponses.WriteAsync(
                context.Response,
                StatusCodes.Status400BadRequest,
                "Некорректный запрос",
                "Параметр запроса содержит недопустимые символы",
                context.RequestAborted);

            return;
        }
    }

    await next(context);
});

if (hasDatabase)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

if (hasDatabase)
{
    var cancellationToken = app.Lifetime.ApplicationStopping;

    // В Production схему применяет отдельный шаг деплоя, приложение миграции не запускает.
    if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MayakDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }

    // Роли и права нужны всегда; dev-пользователей runner сеет только в Development/Testing.
    await app.Services.GetRequiredService<IdentitySeedRunner>().RunAsync(cancellationToken);

    // Каталог и источник для разработки/тестов: composition root, только dev/test.
    // Сид идемпотентен и запускается после пользователей (хотя created_by в seed_dev.sql не задан).
    if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
    {
        await using var seedScope = app.Services.CreateAsyncScope();
        await seedScope.ServiceProvider.GetRequiredService<DevCatalogSeeder>().SeedAsync(cancellationToken);
    }

    app.MapGroup(ApiPrefix).MapIdentityAuthEndpoints();

    // dev-only: в продукте тестового входа нет.
    if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
    {
        app.MapGroup(ApiPrefix).MapDevLoginEndpoint();
    }

    app.MapGroup(ApiPrefix).MapIdentityAdminEndpoints();
    app.MapGroup(ApiPrefix).MapCatalogEndpoints();
    app.MapGroup(ApiPrefix).MapSourcesEndpoints();
    app.MapGroup(ApiPrefix).MapObservationsEndpoints();
    app.MapGroup(ApiPrefix).MapSecretsEndpoints();
    app.MapGroup(ApiPrefix).MapConnectionEndpoints();
    app.MapGroup(ApiPrefix).MapModerationEndpoints();
    app.MapGroup(ApiPrefix).MapMapEndpoints();
}

app.Run();

async Task HandleExceptionAsync(HttpContext context)
{
    var feature = context.Features.Get<IExceptionHandlerFeature>();
    var error = feature?.Error;
    var isBadRequest = error is BadHttpRequestException;

    if (error is not null)
    {
        app.Logger.LogError(
            error,
            "Необработанная ошибка при обработке {Method} {Path}",
            context.Request.Method,
            context.Request.Path);
    }

    await ProblemResponses.WriteAsync(
        context.Response,
        isBadRequest ? StatusCodes.Status400BadRequest : StatusCodes.Status500InternalServerError,
        isBadRequest ? "Некорректный запрос" : "Внутренняя ошибка сервера",
        isBadRequest ? "Запрос не может быть обработан" : "Повторите попытку позже",
        context.RequestAborted);
}

public partial class Program;
