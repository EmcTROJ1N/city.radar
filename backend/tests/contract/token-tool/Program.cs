using BuildingBlocks.Persistence;

using IdentityAccess.Core;
using IdentityAccess.Core.Auth;

using Microsoft.Extensions.DependencyInjection;

// Вспомогательная утилита для tests/contract/run-schemathesis.sh.
// Выпускает access-токены dev-пользователей a1…a5 тем же TokenService, что и Api.
// В продуктовом коде тестового входа нет: утилита живёт только в тестах.

var connectionString = RequireEnvironment("MAYAK_CONNECTION");
var signingKey = RequireEnvironment("MAYAK_JWT_SIGNING_KEY");

var jwtOptions = new JwtOptions
{
    Issuer = Environment.GetEnvironmentVariable("MAYAK_JWT_ISSUER") ?? "mayak",
    Audience = Environment.GetEnvironmentVariable("MAYAK_JWT_AUDIENCE") ?? "mayak",
    SigningKey = signingKey,
};

var services = new ServiceCollection()
    .AddPersistence(connectionString)
    .AddIdentityAccess()
    .AddIdentityAccessAuth(jwtOptions);

await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();

var profiles = scope.ServiceProvider.GetRequiredService<UserProfileService>();
var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();

// GUID совпадают с db/seed_dev.sql и DevSeeder: a1/a2 — user, a3/a4 — moderator, a5 — admin.
(UserRole Role, Guid Id)[] users =
[
    (new UserRole("A1", "user"), Guid.Parse("00000000-0000-0000-0000-0000000000a1")),
    (new UserRole("A2", "user"), Guid.Parse("00000000-0000-0000-0000-0000000000a2")),
    (new UserRole("A3", "moderator"), Guid.Parse("00000000-0000-0000-0000-0000000000a3")),
    (new UserRole("A4", "moderator"), Guid.Parse("00000000-0000-0000-0000-0000000000a4")),
    (new UserRole("A5", "admin"), Guid.Parse("00000000-0000-0000-0000-0000000000a5")),
];

foreach (var (role, userId) in users)
{
    var profile = await profiles.FindAsync(userId, CancellationToken.None)
        ?? throw new InvalidOperationException(
            $"Пользователь {userId} не найден: сначала запустите Api в Testing/Development "
            + "хотя бы один раз, чтобы выполнился dev-seed.");

    var token = tokens.IssueAccess(profile.Id, profile.DisplayName, profile.Permissions);

    // Формат пригоден для eval в bash: export MAYAK_TOKEN_A1='...'
    Console.WriteLine(
        $"export MAYAK_TOKEN_{role.Name}='{token}' # role={role.RoleName}, permissions={profile.Permissions.Count}");
}

return 0;

static string RequireEnvironment(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Не задана переменная окружения {name}.");

internal sealed record UserRole(string Name, string RoleName);
