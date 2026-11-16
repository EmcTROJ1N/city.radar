[← Wiki](README.md) · [Далее: Первый сценарий →](02-first-scenario.md)

# 01. Быстрый старт

> Нужен только Docker. Портал — <http://localhost:4201>, API — <http://localhost:5178>.

## Запуск

```bash
docker compose up -d --build
```

Поднимаются PostGIS, Redis, API и собранный фронтенд на nginx. В Development API сам
применяет миграции, включает dev-сиды и dev-логин.

Проверка:

```bash
curl -s http://localhost:5178/health          # {"status":"ok"}
```

Остановка:

```bash
docker compose down       # данные БД сохраняются
docker compose down -v    # удалить БД и dev-секреты
```

## Вход

Откройте <http://localhost:4201/login> и используйте блок **«Вход для разработки»**.

| Логин | Роль |
|---|---|
| `user` | пользователь |
| `moderator` | модератор |
| `admin` | администратор |

Пароль — `mayak-dev`. Вместо логина можно ввести MAX ID, например `900000005`.

Штатный вход — только через MAX; пока он не реализован (`/auth/max` → `501`).
В production блока dev-входа нет.

[← Wiki](README.md) · [Далее: Первый сценарий →](02-first-scenario.md)
