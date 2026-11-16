# syntax=docker/dockerfile:1.7
#
# IngestionWorker.Host — сбор и разбор источников. Отдельный процесс без HTTP:
# остановка Worker не влияет на Api.
#
# Контекст сборки — каталог backend/. Образ мультиархитектурный (linux/amd64, linux/arm64).
#
#   docker build -f backend/src/Host/IngestionWorker.Host/Dockerfile -t mayak-worker backend

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS restore
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props Mayak.slnx ./

# IngestionWorker.Host и транзитивные ProjectReference.
COPY src/Host/IngestionWorker.Host/IngestionWorker.Host.csproj src/Host/IngestionWorker.Host/
COPY src/Ingestion/Ingestion.Abstractions/Ingestion.Abstractions.csproj src/Ingestion/Ingestion.Abstractions/
COPY src/Ingestion/Ingestion.Pipeline/Ingestion.Pipeline.csproj src/Ingestion/Ingestion.Pipeline/
COPY src/Ingestion/Ingestion.Adapters.TelegramBotApi/Ingestion.Adapters.TelegramBotApi.csproj src/Ingestion/Ingestion.Adapters.TelegramBotApi/
COPY src/Ingestion/Ingestion.Adapters.TelegramMtProto/Ingestion.Adapters.TelegramMtProto.csproj src/Ingestion/Ingestion.Adapters.TelegramMtProto/
COPY src/Ingestion/Ingestion.Llm.OpenAi/Ingestion.Llm.OpenAi.csproj src/Ingestion/Ingestion.Llm.OpenAi/
COPY src/Modules/IdentityAccess/IdentityAccess.Core/IdentityAccess.Core.csproj src/Modules/IdentityAccess/IdentityAccess.Core/
COPY src/Modules/IdentityAccess/IdentityAccess.Contracts/IdentityAccess.Contracts.csproj src/Modules/IdentityAccess/IdentityAccess.Contracts/
COPY src/Modules/Catalog/Catalog.Core/Catalog.Core.csproj src/Modules/Catalog/Catalog.Core/
COPY src/Modules/Catalog/Catalog.Contracts/Catalog.Contracts.csproj src/Modules/Catalog/Catalog.Contracts/
COPY src/Modules/Sources/Sources.Core/Sources.Core.csproj src/Modules/Sources/Sources.Core/
COPY src/Modules/Sources/Sources.Contracts/Sources.Contracts.csproj src/Modules/Sources/Sources.Contracts/
COPY src/BuildingBlocks/BuildingBlocks.Persistence/BuildingBlocks.Persistence.csproj src/BuildingBlocks/BuildingBlocks.Persistence/
COPY src/BuildingBlocks/BuildingBlocks.Messaging/BuildingBlocks.Messaging.csproj src/BuildingBlocks/BuildingBlocks.Messaging/
COPY src/BuildingBlocks/BuildingBlocks.Security/BuildingBlocks.Security.csproj src/BuildingBlocks/BuildingBlocks.Security/
COPY src/BuildingBlocks/BuildingBlocks.Web/BuildingBlocks.Web.csproj src/BuildingBlocks/BuildingBlocks.Web/

RUN dotnet restore src/Host/IngestionWorker.Host/IngestionWorker.Host.csproj

FROM restore AS publish
COPY . .
RUN dotnet publish src/Host/IngestionWorker.Host/IngestionWorker.Host.csproj \
        --configuration Release \
        --output /app \
        --no-restore \
        -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

# Worker не слушает порт: это фоновый процесс. Образ aspnet (а не runtime) нужен потому, что
# модули Core используют FrameworkReference Microsoft.AspNetCore.App (IConfiguration, DI, Kestrel
# не поднимается, но сборка фреймворка требуется загрузчику). Окружение Production обязательно — иначе
# dev-сидеры и мастер-ключ «на процесс» повели бы себя иначе, чем в бою.
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=true

COPY --from=publish --chown=app:app /app ./
COPY --chown=app:app src/Host/entrypoint.sh /app/entrypoint.sh
RUN chmod 0555 /app/entrypoint.sh

# Npgsql на Alpine ищет libgssapi_krb5 для интегрированной аутентификации: без пакета
# загрузчик пишет ошибку в лог (подключение по паролю работает и без него).
RUN apk add --no-cache krb5-libs

USER app

# У Worker нет HTTP: healthcheck проверяет, что процесс с хостом жив (а не зомби/перезапуск).
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=5 \
    CMD pgrep -f "IngestionWorker.Host.dll" > /dev/null || exit 1

ENTRYPOINT ["/app/entrypoint.sh"]
CMD ["dotnet", "IngestionWorker.Host.dll"]
