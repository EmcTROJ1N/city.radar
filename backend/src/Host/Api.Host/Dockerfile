# syntax=docker/dockerfile:1.7
#
# Api.Host — модульный монолит.
#
# Контекст сборки — каталог backend/ (не корень монорепозитория): образ не зависит от фронтенда и
# документации, а COPY ниже перечисляет только csproj из графа Api.Host, чтобы restore кэшировался
# и пересобирался лишь при изменении зависимостей.
#
# Сборка мультиархитектурная (linux/amd64, linux/arm64): базовые образы .NET 10 существуют под обе
# платформы, платформо-специфичного кода нет.
#
#   docker build -f backend/src/Host/Api.Host/Dockerfile -t mayak-api backend

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS restore
WORKDIR /src

# Файлы, влияющие на restore: версии SDK, общие свойства и центральные версии пакетов.
COPY global.json Directory.Build.props Directory.Packages.props Mayak.slnx ./

# Api.Host и транзитивные ProjectReference (граф зависимостей composition root).
COPY src/Host/Api.Host/Api.Host.csproj src/Host/Api.Host/
COPY src/Modules/IdentityAccess/IdentityAccess.Core/IdentityAccess.Core.csproj src/Modules/IdentityAccess/IdentityAccess.Core/
COPY src/Modules/IdentityAccess/IdentityAccess.Contracts/IdentityAccess.Contracts.csproj src/Modules/IdentityAccess/IdentityAccess.Contracts/
COPY src/Modules/Catalog/Catalog.Core/Catalog.Core.csproj src/Modules/Catalog/Catalog.Core/
COPY src/Modules/Catalog/Catalog.Contracts/Catalog.Contracts.csproj src/Modules/Catalog/Catalog.Contracts/
COPY src/Modules/Moderation/Moderation.Core/Moderation.Core.csproj src/Modules/Moderation/Moderation.Core/
COPY src/Modules/Moderation/Moderation.Contracts/Moderation.Contracts.csproj src/Modules/Moderation/Moderation.Contracts/
COPY src/Modules/Sources/Sources.Core/Sources.Core.csproj src/Modules/Sources/Sources.Core/
COPY src/Modules/Sources/Sources.Contracts/Sources.Contracts.csproj src/Modules/Sources/Sources.Contracts/
COPY src/Modules/MapReadModel/MapReadModel.Core/MapReadModel.Core.csproj src/Modules/MapReadModel/MapReadModel.Core/
COPY src/Modules/MapReadModel/MapReadModel.Contracts/MapReadModel.Contracts.csproj src/Modules/MapReadModel/MapReadModel.Contracts/
COPY src/Modules/Observations/Observations.Infrastructure/Observations.Infrastructure.csproj src/Modules/Observations/Observations.Infrastructure/
COPY src/Modules/Observations/Observations.Application/Observations.Application.csproj src/Modules/Observations/Observations.Application/
COPY src/Modules/Observations/Observations.Domain/Observations.Domain.csproj src/Modules/Observations/Observations.Domain/
COPY src/Modules/Observations/Observations.Contracts/Observations.Contracts.csproj src/Modules/Observations/Observations.Contracts/
COPY src/BuildingBlocks/BuildingBlocks.Persistence/BuildingBlocks.Persistence.csproj src/BuildingBlocks/BuildingBlocks.Persistence/
COPY src/BuildingBlocks/BuildingBlocks.Messaging/BuildingBlocks.Messaging.csproj src/BuildingBlocks/BuildingBlocks.Messaging/
COPY src/BuildingBlocks/BuildingBlocks.Security/BuildingBlocks.Security.csproj src/BuildingBlocks/BuildingBlocks.Security/
COPY src/BuildingBlocks/BuildingBlocks.Web/BuildingBlocks.Web.csproj src/BuildingBlocks/BuildingBlocks.Web/
# Sources.Contracts ссылается на Ingestion.Abstractions (общий конверт сообщения).
COPY src/Ingestion/Ingestion.Abstractions/Ingestion.Abstractions.csproj src/Ingestion/Ingestion.Abstractions/

RUN dotnet restore src/Host/Api.Host/Api.Host.csproj

FROM restore AS publish
COPY . .
RUN dotnet publish src/Host/Api.Host/Api.Host.csproj \
        --configuration Release \
        --output /app \
        --no-restore \
        -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

# Порт фиксирован: reverse-proxy знает адрес api:8080, порт наружу не публикуется.
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=true

# app (UID 1654) — встроенный non-root пользователь образов .NET; root в контейнере не нужен.
# krb5-libs: Npgsql на Alpine пытается загрузить libgssapi_krb5 для интегрированной
# аутентификации; без пакета в лог попадает шумная ошибка загрузчика (подключение по паролю
# работает и без него).
RUN apk add --no-cache krb5-libs
COPY --from=publish --chown=app:app /app ./
COPY --chown=app:app src/Host/entrypoint.sh /app/entrypoint.sh
RUN chmod 0555 /app/entrypoint.sh

USER app
EXPOSE 8080

# Маршрут /health не требует БД и секретов и отвечает 200, пока процесс жив.
HEALTHCHECK --interval=15s --timeout=5s --start-period=40s --retries=5 \
    CMD wget -q -O /dev/null http://127.0.0.1:8080/health || exit 1

ENTRYPOINT ["/app/entrypoint.sh"]
# --migrate-only — тот же образ для одноразового шага миграций (deploy/docker-compose.prod.yml).
CMD ["dotnet", "Api.Host.dll"]
