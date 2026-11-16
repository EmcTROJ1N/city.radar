using BuildingBlocks.Messaging;
using BuildingBlocks.Persistence;
using BuildingBlocks.Security;

using Catalog.Core;

using IdentityAccess.Core;

using Ingestion.Llm.OpenAi;

using Sources.Core;
using Sources.Core.Secrets;

namespace IngestionWorker.Host;

/// <summary>
/// Точка входа Worker-а. Явный класс вместо top-level statements: у процесса
/// отдельный composition root, а тип <c>Program</c> в глобальном пространстве имён конфликтовал
/// бы с <c>Api.Host</c> в интеграционных тестах.
/// </summary>
public static class WorkerProgram
{
    private const string ConnectionStringName = "Mayak";

    public static void Main(string[] args)
    {
        var builder = global::Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

        var connectionString = builder.Configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Не задана строка подключения ConnectionStrings:{ConnectionStringName}.");
        }

        var workerOptions = builder.Configuration.GetSection("Worker").Get<WorkerOptions>()
            ?? new WorkerOptions();

        // Composition root Worker-а: только чтение настроек и секретов, никаких HTTP-эндпоинтов
        // и публикации API. Ссылки на *.Core модулей допустимы только здесь.
        builder.Services.AddPersistence(connectionString);
        builder.Services.AddSecretCipher(builder.Environment);
        builder.Services.AddIdentityAccess();
        builder.Services.AddCatalog();
        builder.Services.AddSources();
        builder.Services.AddOutbox();

        // Секреты (сессии Telegram, прокси, ключ LLM) читает только процесс обработки.
        builder.Services.AddSecretReader();
        builder.Services.AddSourceHealthReporter();

        // Провайдер LLM: ключ/модель/прокси перечитываются каждые 30 секунд без перезапуска.
        builder.Services.AddOpenAiProvider();

        builder.Services.AddIngestionWorker(workerOptions);

        var app = builder.Build();

        app.Run();
    }
}
