# Runbook: бэкенд «Городские ресурсы ДНР»

Операционное руководство по production-развёртыванию бэкенда: Api (модульный монолит),
IngestionWorker (сбор и разбор источников), PostgreSQL/PostGIS, Redis, reverse-proxy и бэкапы.

Связанные файлы:

| Файл | Назначение |
|---|---|
| `deploy/docker-compose.prod.yml` | production-стек (api, ingestion-worker, postgres, redis, reverse-proxy, backup, migrate) |
| `deploy/docker-compose.rotate.yml` | override только для ротации мастер-ключа |
| `deploy/.env.example` | шаблон `.env` (без секретов) |
| `deploy/migrate.sh` | обёртка `docker compose run --rm migrate` |
| `deploy/nginx/` | nginx + TLS-заглушка + заголовки безопасности + rate limit |
| `deploy/backup/backup.sh` | ежедневный `pg_dump` с ротацией |
| `backend/src/Host/Api.Host/Dockerfile` | образ Api (он же образ шага `migrate`) |
| `backend/src/Host/IngestionWorker.Host/Dockerfile` | образ Worker |
| `.gitlab-ci.yml` | CI GitLab (build, тесты, Architecture.Tests, Spectral, типы фронтенда, Schemathesis, финальный прогон) |

## 1. Что и как развёрнуто

| Сервис | Образ / сборка | Роль | Сеть | Порты |
|---|---|---|---|---|
| `reverse-proxy` | `deploy/nginx/Dockerfile` (`mayak-proxy`) | TLS, проксирование `/api`, заголовки безопасности, rate limit | `public`, `app` | `${PROXY_HTTP_PORT}`:80, `${PROXY_HTTPS_PORT}`:443 |
| `api` | `backend/src/Host/Api.Host/Dockerfile` (`mayak-api`) | модульный монолит, `/api/v1/*`, `/health` | `app`, `data` | нет (только внутри сети) |
| `ingestion-worker` | `backend/src/Host/IngestionWorker.Host/Dockerfile` (`mayak-worker`) | Telegram/LLM-конвейер, egress в интернет | `app`, `data` | нет |
| `migrate` | тот же образ `mayak-api`, команда `--migrate-only` | одноразовые миграции + роли, до старта api | `app`, `data` | нет |
| `postgres` | `postgis/postgis:16-3.4` | основная БД | `data` (internal) | нет |
| `redis` | `redis:7-alpine` | кэш карты, rate limit | `data` (internal) | нет |
| `backup` | `postgis/postgis:16-3.4` + `backup.sh` | `pg_dump` по расписанию, ротация | `data` | нет |

Важные свойства:

- **Api не зависит от Worker**. В `depends_on` у `api` только `migrate`, `postgres`, `redis`.
- **Api не применяет миграции при старте в Production.** Схему создаёт только сервис `migrate`
  (`Api.Host --migrate-only`), затем процесс завершается с кодом 0 или 1.
- **Секреты** — Docker secrets (`mayak_master_key`, `jwt_signing_key`) и переменные окружения;
  в git они не попадают (`.gitignore`: `.env`, `secrets/`). Мастер-ключ читает
  `BuildingBlocks.Security` из `MAYAK_MASTER_KEY_FILE`, JWT-ключ — entrypoint из `JWT_SIGNING_KEY_FILE`.
  Строка подключения задаётся переменной `ConnectionStrings__Mayak` (или файлом через
  `MAYAK_CONNECTION_STRING_FILE` — entrypoint экспортирует её до старта процесса).
- **Образы мультиархитектурные** (`linux/amd64`, `linux/arm64`), запуск от non-root (`app`, UID 1654).
  Контекст сборки — каталог `backend/`: `docker build -f backend/src/Host/Api.Host/Dockerfile -t mayak-api backend`.

## 2. Требования к хосту

- Docker Engine 24+ и Docker Compose v2 (проверено на Docker 29 / Compose 2.35).
- Свободные порты под reverse-proxy (по умолчанию `8080`/`8443`; 80/443 на хосте обычно заняты).
- ≥ 2 vCPU и ≥ 4 ГБ RAM (лимиты контейнеров: api 2 CPU/1 ГБ, postgres 2 CPU/2 ГБ).
- Исходящий интернет у Worker (Telegram/LLM) и доступ к registry при сборке.
- Для TLS нужен сертификат (см. п. 3.5); без него работает самоподписанная заглушка.

## 3. Первый запуск

Все команды — из каталога `deploy/`.

### 3.1. Секреты

Мастер-ключ шифрования секретов источников — **ровно 32 байта** (AES-256) в base64:

```bash
mkdir -p secrets
chmod 700 secrets
openssl rand -base64 32 > secrets/mayak_master_key

# JWT-ключ подписи: не короче 32 байт; base64 от 48 байт даёт 64 символа.
openssl rand -base64 48 > secrets/jwt_signing_key

chmod 600 secrets/mayak_master_key secrets/jwt_signing_key
```

Проверка формата мастер-ключа (должно быть `32`):

```bash
base64 -d < secrets/mayak_master_key | wc -c
```

> Мастер-ключ нельзя менять/терять: без него существующие секреты (`secrets.ciphertext`) не
> расшифруются. Копия ключа хранится в защищённом хранилище организации, не в репозитории.

### 3.2. `.env`

```bash
cp .env.example .env
# POSTGRES_PASSWORD — уникальный: openssl rand -hex 24
# MAYAK_MASTER_KEY_PATH / JWT_SIGNING_KEY_PATH — пути из п. 3.1
# PROXY_HTTP_PORT / PROXY_HTTPS_PORT — свободные порты хоста
```

`POSTGRES_PASSWORD` попадает в строку подключения Api/Worker и в `pg_dump`-окружение. Файл `.env`
не коммитится; в промышленной эксплуатации пароль выдаётся secret manager-ом.

### 3.3. Миграции

```bash
./migrate.sh
# эквивалент: docker compose -p mayak -f docker-compose.prod.yml run --rm migrate
```

Ожидаемый результат: в логе `Применение миграции …` и `Миграции применены, роли и права приведены
к эталону.`, код выхода `0`. Повторный запуск идемпотентен (EF пропускает применённые миграции,
`RoleSeeder` приводит роли/права к эталону). Dev-пользователи не создаются ни в каком окружении.

Если код выхода `1` — Api не поднимется (см. п. 9).

### 3.4. Запуск стека

```bash
docker compose -p mayak -f docker-compose.prod.yml up -d --build
docker compose -p mayak -f docker-compose.prod.yml ps
```

Все сервисы должны быть `healthy` (у `migrate` — `Exited (0)`). Проверка:

```bash
curl -s http://localhost:8080/health                        # {"status":"ok"}
curl -s "http://localhost:8080/api/v1/map/features?bbox=37.6,47.9,38.1,48.2"   # 200, JSON
curl -sk "https://localhost:8443/api/v1/map/features?bbox=37.6,47.9,38.1,48.2" # 200 по TLS-заглушке
```

### 3.5. TLS

При старте `reverse-proxy` генерирует самоподписанный сертификат в томе `proxy-certs`
(`deploy/nginx/10-self-signed-cert.sh`) — это **заглушка**. Для боевого TLS:

1. Получите сертификат (Let's Encrypt/корпоративный ЦС) на домен портала.
2. Положите `server.crt`/`server.key` в том `proxy-certs` (или смонтируйте секрет в `/etc/nginx/certs`).
3. Перезапустите прокси: `docker compose -p mayak -f docker-compose.prod.yml restart reverse-proxy`.
4. Включите редирект HTTP→HTTPS в `deploy/nginx/nginx.conf` (место помечено `TODO`), сохранив
   `/.well-known/acme-challenge/` для ACME.

## 4. Миграции: как это работает

- Единственный штатный путь — сервис `migrate` (`Api.Host --migrate-only`): применяет EF-миграции из
  `BuildingBlocks.Persistence` и запускает `RoleSeeder`, затем завершается.
- `api` стартует только после `migrate` (`depends_on: condition: service_completed_successfully`).
- В Production Api **не** вызывает `Database.MigrateAsync()` при старте (это осталось только в
  `Development`/`Testing`).
- Вручную: `./migrate.sh` (можно повторять сколько угодно).
- Прямой запуск в контейнере:

  ```bash
  docker compose -p mayak -f docker-compose.prod.yml run --rm migrate --help
  ```

- Отката миграций «вниз» нет: история миграций forward-only, поэтому откат версии приложения
  выполняется восстановлением БД из бэкапа (п. 6.3) либо совместимой миграцией вперёд.

## 5. Health и состояние источников

| Что | Как проверить |
|---|---|
| Api жив | `curl -s http://localhost:8080/health` → `{"status":"ok"}`; `docker compose ps api` → `healthy` |
| Прокси жив | `curl -s http://localhost:8080/healthz` → `ok` |
| Миграции/БД | `docker compose ps postgres` → `healthy`; логи `migrate` |
| Worker жив | `docker compose ps ingestion-worker` → `healthy`; `docker compose logs -f ingestion-worker` |
| Состояние источника | `GET /api/v1/sources/{id}/health` (право `source.manage`) |

Пример запроса состояния источника (нужен access-токен администратора/модератора с правом
`source.manage`; вход через MAX — до его реализации токены выдаются на стенде):

```bash
curl -s -H "Authorization: Bearer $TOKEN" \
  "http://localhost:8080/api/v1/sources/00000000-0000-0000-0000-000000000000/health" | jq
```

Ответ содержит статус подключения, время последней попытки/успеха и ошибку — так проверяются
прокси и доступность Telegram. Список источников: `GET /api/v1/sources` (право `source.manage`).

### 5.1. Системный источник «Сайт»

«Сайт» — неявный источник ручных отметок: он создаётся вместе с категорией (по одному на
категорию) и доступен каждой фиксации этой категории без записи в `map_object_sources`.
Настраивается в админке `pages/sources`: фильтр типа «Сайт», название, включённость, таймаут
актуальности (по умолчанию 3 часа) и статусы. Поля чата, промпта, сессии и прокси у него
скрыты; удалить его нельзя — только вместе с категорией.

Если у «Сайта» нет статусов, пользователь видит «Статусы для отметок ещё не настроены».
Добавьте их в его карточке (`POST /api/v1/sources/{id}/statuses`) или заведите статусы
справочника категории и сохраните категорию — недостающие статусы скопируются в «Сайт».
Worker «Сайт» не читает: у него нет сообщений и адаптера.

## 6. Бэкапы и восстановление

> Команды ниже используют переменные из `deploy/.env`. Загрузите их в текущую оболочку:
> `cd deploy && set -a && . ./.env && set +a` (значения не печатаются в историю команд).

### 6.1. Как устроено

- Сервис `backup` сразу после старта и далее каждые `BACKUP_INTERVAL_SECONDS` (по умолчанию 86400 с)
  выполняет `pg_dump --format=custom` в том `pgbackups` (`/backups/mayak-<UTC>.dump`).
- Успешный дамп отмечается файлом `/backups/.last-success` (его проверяет healthcheck).
- Ротация: файлы старше `BACKUP_RETENTION_DAYS` (по умолчанию 14) удаляются.
- **Ограничение:** это ежедневный полный дамп. RPO ≤ 15 минут требует непрерывной
  WAL-архивации — TODO инфраструктуры; до её включения фактический RPO равен интервалу дампа.

Проверка:

```bash
docker compose -p mayak -f docker-compose.prod.yml exec backup ls -lh /backups
```

### 6.2. Восстановление в отдельную БД (проверка бэкапа)

```bash
# 1. Создать пустую БД
docker compose -p mayak -f docker-compose.prod.yml exec postgres \
  psql -U "$POSTGRES_USER" -d postgres -c 'create database mayak_restore'

# 2. Восстановить дамп (сервис backup уже имеет PGHOST/PGUSER/PGPASSWORD и том /backups)
docker compose -p mayak -f docker-compose.prod.yml run --rm --entrypoint pg_restore backup \
  --format=custom --no-owner --no-privileges \
  -h postgres -U "$POSTGRES_USER" -d mayak_restore /backups/mayak-<UTC>.dump

# 3. Проверить данные
docker compose -p mayak -f docker-compose.prod.yml exec postgres \
  psql -U "$POSTGRES_USER" -d mayak_restore -c 'select count(*) from observations; select count(*) from roles;'

# 4. Удалить проверочную БД
docker compose -p mayak -f docker-compose.prod.yml exec postgres \
  psql -U "$POSTGRES_USER" -d postgres -c 'drop database mayak_restore'
```

### 6.3. Восстановление основной БД (инцидент)

Останавливает запись — выполнять в окно обслуживания.

```bash
docker compose -p mayak -f docker-compose.prod.yml stop api ingestion-worker
# восстановление поверх рабочей БД: --clean удаляет объекты схемы перед созданием
docker compose -p mayak -f docker-compose.prod.yml run --rm --entrypoint pg_restore backup \
  --format=custom --clean --if-exists --no-owner --no-privileges \
  -h postgres -U "$POSTGRES_USER" -d "$POSTGRES_DB" /backups/mayak-<UTC>.dump
./migrate.sh                        # добить миграции, если дамп старее кода
docker compose -p mayak -f docker-compose.prod.yml start api ingestion-worker
curl -s http://localhost:8080/health
```

## 7. Ротация мастер-ключа шифрования секретов

Версии ключей поддерживает `BuildingBlocks.Security`: текущая версия — `MAYAK_MASTER_KEY_VERSION`
(по умолчанию 1), предыдущий ключ — `MAYAK_MASTER_KEY_FILE_PREV` (версия на единицу меньше).
Секреты в БД хранят `secrets.key_version`, поэтому старые значения читаются предыдущим ключом,
новые пишутся текущим.

Порядок (api и worker перезапускаются — короткий простой; старые секреты после перезапуска
читаются предыдущим ключом, новые значения пишутся текущим):

```bash
cd deploy

# 0. Свежий бэкап на случай ошибки
docker compose -p mayak -f docker-compose.prod.yml exec backup \
  pg_dump -h postgres -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc -f /backups/manual-before-rotation.dump

# 1. Старый ключ — в «предыдущий», новый — в текущий
cp secrets/mayak_master_key secrets/mayak_master_key_prev
openssl rand -base64 32 > secrets/mayak_master_key
chmod 600 secrets/mayak_master_key secrets/mayak_master_key_prev

# 2. В .env: версия = 2 (и далее +1 при каждой ротации), путь к предыдущему ключу
#    MAYAK_MASTER_KEY_VERSION=2
#    MAYAK_MASTER_KEY_PREV_PATH=./secrets/mayak_master_key_prev

# 3. Перезапуск api и worker с предыдущим ключом
docker compose -p mayak -f docker-compose.prod.yml -f docker-compose.rotate.yml \
  up -d api ingestion-worker
docker compose -p mayak -f docker-compose.prod.yml ps api ingestion-worker

# 4. Перезаписать значения секретов через админку/API, чтобы они зашифровались версией 2:
#    PUT /api/v1/admin/secrets/llm
#    PUT /api/v1/admin/secrets/proxies/{id}
#    PUT /api/v1/admin/secrets/telegram-sessions/{id}
# (значения нигде не логируются и наружу не отдаются —)
#    До перезаписи старые секреты продолжают работать: их читает предыдущий ключ.

# 5. Убедиться, что новых ошибок расшифровки нет
docker compose -p mayak -f docker-compose.prod.yml logs --since 10m api ingestion-worker | grep -i "key\|ключ" || true

# 6. Убрать предыдущий ключ: перезапуск без override и удаление файла
docker compose -p mayak -f docker-compose.prod.yml up -d api ingestion-worker
rm secrets/mayak_master_key_prev
```

Важно: после шага 3 ни в коем случае не удаляйте `secrets/mayak_master_key_prev`, пока не
перезаписаны все секреты. Если ключ потерян, значения секретов не восстановить — их придётся
вводить заново (LLM-ключ, прокси, Telegram-сессии).

## 8. Worker: остановка и независимость Api

```bash
# Остановить только Worker
docker compose -p mayak -f docker-compose.prod.yml stop ingestion-worker

# Api, карта и health должны продолжать работать
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:8080/health
curl -s -o /dev/null -w '%{http_code}\n' \
  "http://localhost:8080/api/v1/map/features?bbox=37.6,47.9,38.1,48.2"
docker compose -p mayak -f docker-compose.prod.yml ps api     # healthy

# Вернуть Worker
docker compose -p mayak -f docker-compose.prod.yml start ingestion-worker
```

Ручные отметки (`POST /api/v1/observations`) обслуживает Api и они не зависят от Worker; в проде
для ручной проверки нужен access-токен (вход через MAX —), в dev/тестах сценарий покрыт
`Api.IntegrationTests/Observations` и `Api.IntegrationTests/Map`.

## 9. Откат версии

1. Приложение: соберите/возьмите предыдущий тег и поднимите стек с ним:
   ```bash
   MAYAK_IMAGE_TAG=<previous> docker compose -p mayak -f docker-compose.prod.yml up -d
   ```
   Схема БД при этом должна быть совместима с предыдущей версией (миграции forward-only).
2. Если новая миграция несовместима — восстановите БД из дампа (п. 6.3), затем поднимите
   предыдущий образ.
3. Проверка после отката: `curl /health`, `curl /api/v1/map/features?bbox=…`, `docker compose ps`.

## 10. Диагностика типовых проблем

| Симптом | Причина и действие |
|---|---|
| `migrate` завершился с кодом 1 | нет `ConnectionStrings__Mayak` / БД недоступна / ошибка SQL. Смотрите `docker compose logs migrate` |
| `api` не стартует: `Не задан Jwt:SigningKey` | не смонтирован секрет `jwt_signing_key` или пуст файл (`JWT_SIGNING_KEY_FILE`) |
| `api` не стартует: `Не задана переменная окружения MAYAK_MASTER_KEY_FILE` | нет секрета мастер-ключа; проверьте `secrets/` и `MAYAK_MASTER_KEY_PATH` |
| `api` не стартует: `UnknownKeyVersion` | в БД секреты с версией, которой нет в `MAYAK_MASTER_KEY_FILE_PREV` — верните предыдущий ключ |
| Прокси отдаёт 503 `application/problem+json` | Api не healthy: `docker compose ps api`, `docker compose logs api` |
| Прокси отдаёт 429 | сработал rate limit nginx (50 r/s на IP, burst 100) или лимит приложения в Redis |
| Worker перезапускается | `docker compose logs ingestion-worker`; чаще всего нет БД/мастер-ключа или ошибка адаптера источника |
| `backup` unhealthy | `pg_dump` падает: проверьте `PGPASSWORD`/доступ к postgres и свободное место в томе `pgbackups` |

Логи: `docker compose -p mayak -f docker-compose.prod.yml logs -f <service>`.
Секреты в логи не попадают (значения не печатаются ни приложением, ни entrypoint-ом).

## 11. Чек-лист ручной проверки перед приёмкой

Отмечать на стенде, максимально приближенном к production-конфигурации (Production, TLS, прокси).
- [ ] **Первый запуск:** `./migrate.sh` → 0; `up -d --build` → все `healthy`; `migrate` — `Exited (0)`.
- [ ] **Карта:** `GET /api/v1/map/features?bbox=37.6,47.9,38.1,48.2` через прокси (HTTP и HTTPS) → 200.
- [ ] **Ошибки:** неверный bbox → 400 `application/problem+json`; 429 от прокси — тоже `application/problem+json`.
- **:** `docker compose stop ingestion-worker` → карта и `/health` отвечают 200, `api` остаётся
      `healthy`; ручная отметка создаётся (при наличии токена). `docker compose start ingestion-worker` → Worker `healthy`.
- ** (прокси):** задать прокси в админке (`PUT /api/v1/admin/secrets/proxies/{id}`), назначить
      источнику (`PUT /api/v1/admin/sources/{id}/connection`); в логах Worker — подключения только через
      прокси; при недоступном прокси прямых подключений нет (ошибка в `/sources/{id}/health`).
- ** (LLM без перезапуска):** `PUT /api/v1/admin/secrets/llm` с новым ключом/моделью; Worker
      подхватывает в течение ~30 с (опрос настроек), `docker inspect -f '{{.State.StartedAt}}'` не меняется.
- **:** `GET /api/v1/admin/secrets` не содержит значений; в ответах и логах нет `ciphertext`.
- [ ] **Секреты:** `secrets/` и `.env` не в git (`git status --porcelain` пуст по этим путям).
- [ ] **Бэкап:** в `/backups` появился свежий `mayak-*.dump`, `.last-success` обновлён; восстановление
      в отдельную БД (п. 6.2) проходит.
- [ ] **Ротация ключа:** п. 7 выполнен на стенде; после шага 3 секреты читаются, после перезаписи —
      работают с версией 2.
- [ ] **TLS:** сертификат не самоподписанный (или заглушка осознанно оставлена до пилота), HSTS и
      заголовки безопасности присутствуют в ответах.

### 11.1. — реальное сообщение из Telegram-чата

Проверяется на стенде, приближенном к production, с реальной сессией Telegram и реальным
чатом. Автотесты `Api.IntegrationTests/Ingestion` и `Ingestion.Pipeline.Tests` используют
fake-источник и fake-LLM, поэтому этот сценарий ими не закрывается.

Предусловия: `api` и `ingestion-worker` — `healthy`; есть access-токен администратора и
модератора (вход через MAX — на стенде токены выдаются штатным способом стенда);
источник типа `telegram_chat` включён, у него есть промпт, статусы и привязанная фиксация
(`PUT /api/v1/catalog/objects/{id}/sources/{sourceId}`); сессия Telegram загружена
(`PUT /api/v1/admin/secrets/telegram-sessions/{id}`) и назначена источнику
(`PUT /api/v1/admin/sources/{id}/connection`, `telegramSessionId`).

1. Проверить, что источник подключён: `GET /api/v1/sources/{id}/health` → `state` не
   `auth_error`; после активности в чате обновляется `lastMessageAt`. В логах Worker нет
   ошибок авторизации и прокси.
2. Отправить в чат **уверенное** сообщение, однозначно попадающее в промпт источника и
   содержащее статус из его списка (например, «Воды нет, трубу прорвало на Ленина, 5»).
3. В течение двух интервалов опроса pipeline проверить:
   - `raw_messages` содержит строку с `source_id` источника и текстом сообщения;
   - `observations` содержит наблюдение с `source_type = 'telegram'` и `raw_message_id`
     этого сообщения;
   - `GET /api/v1/map/features?bbox=…` или `GET /api/v1/map/objects/{objectId}` показывает
     новый статус, если наблюдение самое свежее и актуальное.
4. Отправить **неуверенное** сообщение: размытое («вроде где-то дали воду»), либо про
   фиксацию, которой нет в списке источника, либо с низким confidence. Ожидаемо: сообщение
   сохраняется в `raw_messages`, карта не меняется, а в
   `GET /api/v1/moderation/cases?state=open` появляется кейс с `kind` из
   `low_confidence`, `object_not_found`, `invalid_status` или `invalid_response`.
5. Повторно доставить то же сообщение (перезапуск адаптера или повторный polling при
   поддержке источника). Ожидаемо: дубля в `observations` нет (уникальность
   `raw_messages(source_id, external_id)` и идемпотентность обработчика).
6. Убедиться, что `docker inspect -f '{{.State.StartedAt}}' ingestion-worker` не изменился
   за время шагов 2–5.

**Ожидаемый результат:** уверенное сообщение доходит до карты, неуверенное — до очереди
модерации; дублей нет; Worker не перезапускается. Если сообщение не появилось ни на карте,
ни в очереди — проверить `raw_messages`, `outbox_messages` (`type`, `attempts`, `last_error`)
и логи Worker.

### 11.2. — реальный прокси и Telegram без прямых подключений

Логика выбора прокси и запрет прямого соединения покрыты `ProxyTests` и
`OpenAiProviderTests`, но реальный SOCKS5/HTTP/MTProxy и сетевой egress проверяются здесь.

1. Загрузить прокси администратором:
   `PUT /api/v1/admin/secrets/proxies/{id}` с `scheme` = `socks5` или `mtproxy`,
   `host`, `port`, при необходимости `username`/`password` (для MTProxy секрет передаётся
   в `password`). Значение доступно только на запись — прочитать его нельзя.
2. Назначить прокси источнику:
   `PUT /api/v1/admin/sources/{id}/connection` с `telegramSessionId` и `proxyId`. Если
   источник уже работает, перезапуск Worker не требуется: supervisor пересоздаёт адаптер
   источника при изменении конфигурации.
3. В течение ~30 секунд проверить `GET /api/v1/sources/{id}/health` → `state = ok`,
   `lastMessageAt` обновляется после сообщений в чате, в логах Worker нет ошибок прокси.
4. Убедиться, что прямого egress к Telegram нет. На хосте Worker включить перехват на
   время проверки:
   `tcpdump -ni any 'host 149.154.160.0/20 or host 91.108.4.0/22'`.
   Ожидаемо: пакеты идут только к адресу прокси, а не к подсетям Telegram DC. Альтернатива
   без tcpdump — заблокировать в firewall подсети Telegram DC и убедиться, что сообщения
   продолжают приходить через прокси.
5. Сделать прокси недоступным: остановить прокси или заменить `host`/`port` на закрытый
   порт через `PUT /api/v1/admin/secrets/proxies/{id}`. Ожидаемо:
   `GET /api/v1/sources/{id}/health` → `error` или `flood_wait` (не `ok`), новые сообщения
   не приходят, Worker продолжает работать и **не** открывает прямое соединение с Telegram.
6. Вернуть рабочий прокси через UI/API. Ожидаемо: источник восстанавливается без
   перезапуска Worker; `docker inspect -f '{{.State.StartedAt}}' ingestion-worker` не
   изменился.

**Ожидаемый результат:** при заданном прокси соединения идут только через него; при
недоступном прокси прямых подключений нет; смена прокси применяется без перезапуска.

### 11.3. Приёмка качества LLM на наборе заказчика

Инструмент и формат набора описаны в `datasets/llm-eval/README.md`. Пока размеченного
набора 200–500 сообщений нет, статус — «инструмент готов, набор не предоставлен».
Проверка на синтетическом `sample.jsonl` или с `--provider fake` **не является** оценкой
качества модели.

1. Получить от заказчика обезличенный JSONL-набор 200–500 сообщений и конфигурацию
   реального источника (`--sources`: статусы, фиксации, промпт, режим `linkByText`, порог
   confidence). Набор и отчёты с персональными данными в репозиторий не коммитить.
2. Из каталога `backend` выполнить:
   ```bash
   export MAYAK_LLM_API_KEY=...        # ключ не печатается и не попадает в отчёт
   export MAYAK_LLM_MODEL=...
   export MAYAK_LLM_BASE_URL=...       # при необходимости
   dotnet run --project tests/Ingestion.LlmEval -- \
     --dataset ../datasets/llm-eval/<набор>.jsonl \
     --sources ../datasets/llm-eval/<источник>.json \
     --provider openai \
     --out ../datasets/llm-eval/<отчёт>.md
   ```
3. Ожидаемый результат: код возврата `0` означает успешный прогон, а не достижение
   целевых значений; сами значения проверяются по Markdown/JSON-отчёту: `precision ≥ 0,90`,
   `extractorRecall ≥ 0,70`, `linkerRecall ≥ 0,70` (если метрика применима). Ключ LLM
   отсутствует в отчёте и логах. JSON-отчёт сохранить как baseline.
4. Если порог не достигнут — зафиксировать фактические метрики и случаи ошибок в отчёте,
   не подгонять разметку под ответы модели. Повторный прогон после изменения промпта или
   модели сравнивать с сохранённым baseline (`--baseline`); для регрессионного режима можно
   задать абсолютный порог `--min-precision 0.9`.

**Ожидаемый результат:** целевые precision/recall подтверждены на согласованном наборе,
отчёт приложен к приёмке. До получения набора остаётся открытым.

## 12. Что не покрыто этим runbook-ом

- Статика фронтенда: reverse-proxy её не раздаёт (сборка Angular — отдельный шаг; CSP в
  `deploy/nginx/snippets/security-headers.conf` ужесточена под JSON и помечена TODO).
- WAL-архивация и PITR — TODO инфраструктуры; ежедневный `pg_dump` её не заменяет.
- Вход через MAX: до реализации эндпоинтов выдачи токенов ручные проверки, требующие
  авторизации, выполняются на dev-стенде или автотестами.
- Мониторинг/метрики исключены из продукта; здесь только healthcheck-и Docker.
