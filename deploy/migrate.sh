#!/usr/bin/env bash
# Обёртка над одноразовым шагом миграций production-развёртывания.
#
#   ./migrate.sh            # применить миграции и роли на БД из deploy/.env
#   ./migrate.sh --help     # аргументы передаются контейнеру migrate
#
# Повторный запуск идемпотентен. Сервис migrate поднимает зависимости (postgres) и удаляется
# после завершения (--rm). Код выхода — код Api.Host --migrate-only: 0 успех, 1 ошибка.
set -euo pipefail

cd "$(dirname "$0")"

project="${COMPOSE_PROJECT_NAME:-mayak}"
compose_file="${COMPOSE_FILE:-docker-compose.prod.yml}"
env_file="${MAYAK_ENV_FILE:-.env}"

compose_args=(-p "$project" -f "$compose_file")
if [[ -f "$env_file" ]]; then
    compose_args+=(--env-file "$env_file")
else
    echo "migrate.sh: файл $env_file не найден — используйте cp .env.example .env" >&2
    exit 1
fi

exec docker compose "${compose_args[@]}" run --rm migrate "$@"
