# Проверка контракта Schemathesis

`run-schemathesis.sh` поднимает настоящий `Api.Host` на dev-БД и прогоняет по нему
[Schemathesis](https://schemathesis.readthedocs.io/) — генератор запросов, который проверяет
ответы на соответствие `api/openapi.yaml`. Это дополнение к интеграционным тестам
(`backend/tests/Api.IntegrationTests`), а не их замена: xUnit проверяет бизнес-правила на
заранее выбранных примерах, Schemathesis — граничные и невалидные запросы, которые никто
не написал руками.

## Что проверяется

- `--checks all`: серверные ошибки (`500`), код ответа объявлен в контракте
  (`status_code_conformance`), тело соответствует схеме (`response_schema_conformance`),
  `Content-Type` совпадает с контрактом, невалидные данные отклоняются.
- Отдельные прогоны с токенами seed-пользователей: анонимно, `user` (a1), `moderator` (a3),
  `admin` (a5). Один заголовок `Authorization` на прогон — поэтому роли не смешиваются.
- Токены выпускает `token-tool` тем же `TokenService`, что и Api, читая пользователей и права
  из БД. В продуктовом коде тестового входа нет.

**Успех** — команда завершилась с кодом `0` и в выводе Schemathesis нет `FAILED`/`ERROR`:
ни одного 500, ни одного ответа вне объявленных кодов, ни одного нарушения схемы.
**Провал** — ненулевой код выхода: из вывода видно метод, путь и в чём расхождение; исправлять
нужно код Api (или осознанно контракт по правилам `api/README.md`), а не отключать проверку.

## Установка

```bash
pipx install schemathesis     # рекомендуется: изолированное окружение
# если pipx нет:
brew install pipx && pipx install schemathesis
# либо разово, без установки:
pipx run schemathesis --version
```

Скрипт сам проверяет наличие `schemathesis` и печатает эту инструкцию, если его нет.

## Запуск

```bash
cp deploy/.env.example deploy/.env     # один раз: пароль Postgres
./backend/tests/contract/run-schemathesis.sh
```

Скрипт:

1. поднимает `postgres`/`redis` из `deploy/docker-compose.yml` + `deploy/docker-compose.override.yml`
   (пропустить — `--skip-docker` или `MAYAK_SKIP_DOCKER=1`);
2. собирает `Api.Host` и `tests/contract/token-tool` в Release;
3. запускает Api в окружении `Testing` на `http://127.0.0.1:5099` с dev-seed
   (пользователи a1…a5, источник «Чат жильцов Калининского», фиксация «Калининский район»);
4. выпускает токены a1…a5 и запускает Schemathesis по `http://127.0.0.1:5099/api/v1`;
5. останавливает Api (оставить запущенным — `--keep-api`).

Параметры:

| Параметр | Переменная | По умолчанию |
| --- | --- | --- |
| `--role <anonymous\|user\|moderator\|admin\|a1…a5\|all>` | `MAYAK_SCHEMATHESIS_ROLE` | `all` |
| `--port <номер>` | `MAYAK_API_PORT` | `5099` |
| `--skip-docker` | `MAYAK_SKIP_DOCKER=1` | выключено |
| `--keep-api` | `MAYAK_KEEP_API=1` | выключено |

Примеры:

```bash
# только админ: быстрый прогон с максимальными правами
./backend/tests/contract/run-schemathesis.sh --role admin

# Api уже запущен вручную на 5178, Docker поднимать не нужно
./backend/tests/contract/run-schemathesis.sh --skip-docker --port 5178 --role all
```

Строка подключения по умолчанию собрана из `deploy/.env`
(`Host=127.0.0.1;Port=$POSTGRES_PORT;Database=$POSTGRES_DB;Username=$POSTGRES_USER;
Password=$POSTGRES_PASSWORD`); целиком переопределяется `MAYAK_CONNECTION_STRING`.
Ключ подписи тестовых токенов — `MAYAK_JWT_SIGNING_KEY` (по умолчанию тестовый, не короче
32 байт); Api и `token-tool` получают один и тот же ключ.

## Ограничения

- Schemathesis создаёт и меняет данные (источники, категории, отметки) — запускайте только на
  dev/test-БД, не на проде.
- Прогон `all` занимает заметно больше времени, чем `--role admin`.
- Лог Api пишется в `$TMPDIR/mayak-schemathesis-api.log`; при падении старта скрипт печатает
  его хвост.
- Контракт — источник правды: если Schemathesis находит расхождение, сначала проверьте, не
  ошибка ли в реализации. Изменение `api/openapi.yaml` требует `cd api && npm run check`.
