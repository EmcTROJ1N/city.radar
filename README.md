# Городские ресурсы — карта доступности воды

Портал мониторинга городских ресурсов: публичная карта, на которой видно, где сейчас есть вода,
откуда пришли данные и насколько они свежие. Источники — ручные отметки жителей и автоматический
разбор Telegram-чатов с помощью LLM, с обязательной модерацией и полным журналом действий.

Проект — модульный монолит на .NET и PWA на Angular. Ядро не привязано к конкретному ресурсу:
категории, типы объектов, статусы и источники настраиваются в интерфейсе во время работы.

## Что умеет

- **Карта**: объекты и геозоны, текущий статус как проекция последних наблюдений, фильтры,
  ETag и кэш в Redis, отрисовка на MapLibre GL JS.
- **Отметки**: «увидел — отметил» с телефона; наблюдения append-only, отзыв вместо правки.
- **Источники**: Telegram (Bot API и MTProto), извлечение статусов через LLM, health источников,
  обязательный прокси для MTProto.
- **Модерация**: очередь предложенных наблюдений, жалобы, аномалии, одобрение и отклонение.
- **Админка**: пользователи и роли (RBAC на permission-claims), каталог, источники, секреты,
  настройки, журнал аудита.
- **PWA**: офлайн-оболочка, индикатор отсутствия сети, обновление версии.

## Стек

| Слой | Технологии |
|---|---|
| Бэкенд | .NET 10, ASP.NET Core Minimal API, EF Core + Npgsql, PostgreSQL 16 + PostGIS, Redis |
| Воркер | .NET 10 Worker, WTelegramClient, OpenAI-совместимый LLM |
| Фронтенд | Angular 21 (standalone, signals, zoneless), MapLibre GL JS, Feature-Sliced Design, Vitest |
| Контракт | OpenAPI 3.1 (`api/openapi.yaml`), Spectral, Schemathesis |
| Инфраструктура | Docker Compose, nginx, GitLab CI (`.gitlab-ci.yml`) |

## Структура репозитория

```
api/        контракт OpenAPI — источник правды для бэкенда, фронтенда и тестов
backend/    Api.Host (модульный монолит), IngestionWorker.Host, модули, тесты
frontend/   Angular-приложение по FSD
db/         эталонная схема (schema.sql) и сиды — с ними сверяются EF-миграции
deploy/     docker-compose (dev/prod), nginx, бэкапы, миграции
docs/       runbook по эксплуатации
datasets/   примеры датасета для оценки качества LLM-извлечения
```

## Запуск одной командой (Docker Compose)

Нужен только Docker. Из корня репозитория:

```bash
docker compose up -d --build
```

Поднимаются PostGIS, Redis, Api, собранный фронтенд на nginx и одноразовый шаг генерации
dev-секретов. Портал — **http://localhost:4200** (nginx раздаёт Angular и проксирует `/api/` в Api),
Api напрямую — http://localhost:5178 (`/health`).

Контейнеры работают в `ASPNETCORE_ENVIRONMENT=Development`: Api сам применяет миграции при старте,
включены dev-сиды и dev-логин (`admin` / `moderator` / `user`, пароль `mayak-dev`). Мастер-ключ
шифрования и ключ подписи JWT генерируются один раз в томе `dev-secrets` — они общие для Api и
Worker и переживают перезапуск контейнеров.

Воркер сбора источников вынесен за профиль `ingestion` (ему нужны настроенные Telegram/LLM):

```bash
docker compose --profile ingestion up -d --build
```

Остановка и очистка:

```bash
docker compose down        # остановить, данные БД остаются в томе pgdata
docker compose down -v     # удалить и данные БД, и dev-секреты
```

Порты переопределяются переменными окружения (`FRONTEND_PORT`, `API_PORT`, `POSTGRES_PORT`,
`REDIS_PORT`) — в корневом `.env` или в командной строке. Production-развёртывание с TLS,
внешними секретами и бэкапами — `deploy/docker-compose.prod.yml` (см. `docs/runbook-backend.md`).

Для hot-reload фронтенда поднимите только инфраструктуру и Api, а `ng serve` запустите локально:

```bash
docker compose up -d postgres redis api
cd frontend && npm start     # :4200, /api/v1 проксируется на :5178
```

## Быстрый старт без контейнеров

Нужны .NET SDK 10 (версия зафиксирована в `backend/global.json`), Node.js 22 и Docker.

### 1. База и кэш

```bash
cd deploy
cp .env.example .env      # для локальной разработки значения по умолчанию подходят
docker compose up -d postgres redis
```

Postgres слушает `127.0.0.1:5432`, Redis — `127.0.0.1:6379`.

### 2. Бэкенд

В Development миграции применяются при старте, сиды разработки подставляются автоматически,
мастер-ключ шифрования и ключ подписи JWT генерируются на процесс.

```bash
cd backend
export ConnectionStrings__Mayak="Host=localhost;Port=5432;Database=mayak;Username=mayak;Password=mayak_dev"
dotnet run --project src/Host/Api.Host      # http://localhost:5178, health: /health
```

### 3. Фронтенд

```bash
cd frontend
npm install
npm start                                    # http://localhost:4200, /api/v1 проксируется на :5178
```

### Вход для разработки

На странице «Вход» есть блок только для Development. Логин — MAX ID пользователя из базы
(`900000005`) или `admin` / `moderator` / `user`, пароль — `DevAuth:Password` из
`appsettings.Development.json` (`mayak-dev`). В production-сборке блока нет, а `/auth/dev-login`
не зарегистрирован.

### Воркер (опционально)

Сбор данных из Telegram требует настроенного источника и прокси; для работы с картой и админкой
воркер не нужен:

```bash
dotnet run --project src/Host/IngestionWorker.Host
```

## Контракт API

`api/openapi.yaml` — источник правды: бэкенд, фронтенд и тесты подчиняются ему, а не наоборот.

```bash
cd api && npm install
npm run check    # Spectral + перегенерация типов фронтенда
npm run mock     # Prism на http://127.0.0.1:4010
npm run docs     # статическая документация в api/dist
```

Подробности — в `api/README.md`.

## Тесты и проверки

```bash
# бэкенд
cd backend
dotnet build -warnaserror
dotnet test                       # unit, интеграционные (Testcontainers), архитектурные

# фронтенд
cd frontend
npm run verify                    # типы, тесты скриптов, production-сборка, тесты, линтер FSD

# контракт
cd api && npm run lint
```

Интеграционные тесты поднимают Postgres/PostGIS и Redis через Testcontainers — нужен запущенный
Docker. CI описана в `.gitlab-ci.yml`.

## Секреты и безопасность

- Пароли БД, мастер-ключ и ключ подписи JWT в репозиторий не попадают: `.env` и `deploy/secrets/`
  в `.gitignore`, в репозитории лежит только шаблон `deploy/.env.example`.
- В production мастер-ключ и JWT-ключ монтируются как Docker secrets и обязательны: без них
  приложение не стартует.
- Секреты источников шифруются AES-256-GCM; через API наружу отдаются только вид, название и
  признак «значение задано».
- Все действия модераторов и администраторов пишутся в append-only журнал аудита.

## Документация

- `docs/runbook-backend.md` — развёртывание, миграции, ротация ключей, бэкапы, диагностика.
- `api/README.md`, `frontend/README.md` — детали по контракту и клиенту.
