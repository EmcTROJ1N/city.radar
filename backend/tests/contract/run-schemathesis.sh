#!/usr/bin/env bash
#
# Прогон Schemathesis по api/openapi.yaml против настоящего Api.
#
# Скрипт собирает и поднимает Api.Host в окружении Testing на БД из deploy/docker-compose
# (dev-seed: пользователи a1…a5, источник и фиксация), выпускает access-токены тем же
# TokenService, что и Api (tests/contract/token-tool), и запускает
# `schemathesis run ... --checks all` для анонимного доступа и ролей seed-пользователей.
#
# Успех — schemathesis завершился с кодом 0: нет серверных ошибок (500) и нарушений схемы,
# все коды ответов объявлены в контракте. Скрипт не заменяет интеграционные тесты:
# Schemathesis генерирует граничные и невалидные запросы.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BACKEND_DIR="$(cd "${SCRIPT_DIR}/../.." && pwd)"
ROOT_DIR="$(cd "${BACKEND_DIR}/.." && pwd)"

SPEC_PATH="${ROOT_DIR}/api/openapi.yaml"
API_PROJECT="${BACKEND_DIR}/src/Host/Api.Host/Api.Host.csproj"
TOKEN_PROJECT="${BACKEND_DIR}/tests/contract/token-tool/Contract.TokenTool.csproj"

PORT="${MAYAK_API_PORT:-5099}"
ROLE="${MAYAK_SCHEMATHESIS_ROLE:-all}"
SKIP_DOCKER="${MAYAK_SKIP_DOCKER:-0}"
KEEP_API="${MAYAK_KEEP_API:-0}"

JWT_ISSUER="${MAYAK_JWT_ISSUER:-mayak-schemathesis}"
JWT_AUDIENCE="${MAYAK_JWT_AUDIENCE:-mayak-schemathesis}"
JWT_SIGNING_KEY="${MAYAK_JWT_SIGNING_KEY:-mayak-schemathesis-signing-key-0123456789}"
JWT_ACCESS_MINUTES="${MAYAK_JWT_ACCESS_MINUTES:-240}"

LOG_FILE="${TMPDIR:-/tmp}/mayak-schemathesis-api.log"

usage() {
  cat <<'EOF'
Использование: run-schemathesis.sh [параметры]

Параметры:
  --role <anonymous|user|moderator|admin|a1|a2|a3|a4|a5|all>
                        Чей токен подставлять (по умолчанию all: анонимно + user a1,
                        moderator a3, admin a5).
  --port <номер>        Порт Api (по умолчанию 5099; env MAYAK_API_PORT).
  --skip-docker         Не поднимать postgres/redis через docker compose.
  --keep-api            Не останавливать Api после прогона (для разбора вручную).
  -h, --help            Эта справка.

Переменные окружения:
  POSTGRES_USER, POSTGRES_PASSWORD, POSTGRES_DB, POSTGRES_PORT  — из deploy/.env
  MAYAK_CONNECTION_STRING  — переопределить строку подключения целиком
  MAYAK_JWT_SIGNING_KEY    — ключ подписи тестовых токенов (>= 32 байт)
  MAYAK_SKIP_DOCKER=1      — то же, что --skip-docker
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --role) ROLE="${2:?--role требует значение}"; shift 2 ;;
    --port) PORT="${2:?--port требует значение}"; shift 2 ;;
    --skip-docker) SKIP_DOCKER=1; shift ;;
    --keep-api) KEEP_API=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Неизвестный параметр: $1" >&2; usage >&2; exit 2 ;;
  esac
done

case "${ROLE}" in
  all|anonymous|user|moderator|admin|a1|a2|a3|a4|a5) ;;
  *) echo "Неизвестная роль '${ROLE}'. Допустимо: all, anonymous, user, moderator, admin, a1…a5." >&2; exit 2 ;;
esac

if [[ ! -f "${SPEC_PATH}" ]]; then
  echo "Не найден контракт: ${SPEC_PATH}" >&2
  exit 1
fi

# --- Schemathesis ------------------------------------------------------------------------

if ! command -v schemathesis >/dev/null 2>&1; then
  cat >&2 <<'EOF'
Schemathesis не найден в PATH.

Установка (рекомендуется pipx — изолированное окружение, без глобальных пакетов):
  pipx install schemathesis

Если pipx нет:
  python3 -m pip install --user schemathesis      # или: brew install pipx && pipx install schemathesis

Разовый запуск без установки (после него повторите скрипт):
  pipx run schemathesis --version

Документация: https://schemathesis.readthedocs.io/
EOF
  exit 127
fi

echo "Schemathesis: $(schemathesis --version 2>/dev/null || echo 'версия не определена')"

# --- Параметры БД из deploy/.env ---------------------------------------------------------

if [[ -f "${ROOT_DIR}/deploy/.env" ]]; then
  set -a
  # shellcheck disable=SC1091
  . "${ROOT_DIR}/deploy/.env"
  set +a
fi

POSTGRES_USER="${POSTGRES_USER:-mayak}"
POSTGRES_DB="${POSTGRES_DB:-mayak}"
POSTGRES_PORT="${POSTGRES_PORT:-5432}"

# Отдельная БД для фаззинга: Schemathesis создаёт и меняет данные, а прогон должен быть
# воспроизводимым. По умолчанию она пересоздаётся при каждом запуске (MAYAK_RESET_DB=0 — нет).
SCHEMATHESIS_DB="${MAYAK_SCHEMATHESIS_DB:-mayak_schemathesis}"
if [[ ! "${SCHEMATHESIS_DB}" =~ ^[A-Za-z0-9_]+$ ]]; then
  echo "Недопустимое имя БД '${SCHEMATHESIS_DB}': только латинские буквы, цифры и _." >&2
  exit 2
fi

: "${POSTGRES_PASSWORD:?Задайте POSTGRES_PASSWORD в deploy/.env (cp deploy/.env.example deploy/.env)}"

CONNECTION_STRING="${MAYAK_CONNECTION_STRING:-Host=127.0.0.1;Port=${POSTGRES_PORT};Database=${SCHEMATHESIS_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}}"

# --- Docker: postgres + redis ------------------------------------------------------------

compose() {
  local env_args=()
  if [[ -f "${ROOT_DIR}/deploy/.env" ]]; then
    env_args=(--env-file "${ROOT_DIR}/deploy/.env")
  fi

  docker compose "${env_args[@]}" \
    -f "${ROOT_DIR}/deploy/docker-compose.yml" \
    -f "${ROOT_DIR}/deploy/docker-compose.override.yml" "$@"
}

if [[ "${SKIP_DOCKER}" != "1" ]]; then
  if command -v docker >/dev/null 2>&1; then
    echo "Поднимаю postgres и redis из deploy/docker-compose…"
    compose up -d postgres redis

    for _ in $(seq 1 30); do
      if compose exec -T postgres pg_isready -U "${POSTGRES_USER}" -d "${POSTGRES_DB}" >/dev/null 2>&1; then
        break
      fi
      sleep 1
    done

    if [[ "${MAYAK_RESET_DB:-1}" == "1" ]]; then
      echo "Пересоздаю тестовую БД ${SCHEMATHESIS_DB}…"
      compose exec -T postgres psql -U "${POSTGRES_USER}" -d postgres -v ON_ERROR_STOP=1 \
        -c "drop database if exists \"${SCHEMATHESIS_DB}\" with (force)" \
        -c "create database \"${SCHEMATHESIS_DB}\""
    fi
  else
    echo "Docker не найден: предполагаю, что Postgres/Redis уже запущены." >&2
  fi
fi

# --- Сборка --------------------------------------------------------------------------------

echo "Собираю Api и утилиту токенов (Release)…"
dotnet build "${API_PROJECT}" -c Release --nologo -v:q
dotnet build "${TOKEN_PROJECT}" -c Release --nologo -v:q

API_DLL="${BACKEND_DIR}/src/Host/Api.Host/bin/Release/net10.0/Api.Host.dll"
TOKEN_DLL="${BACKEND_DIR}/tests/contract/token-tool/bin/Release/net10.0/Contract.TokenTool.dll"

[[ -f "${API_DLL}" ]] || { echo "Не найден ${API_DLL}" >&2; exit 1; }
[[ -f "${TOKEN_DLL}" ]] || { echo "Не найден ${TOKEN_DLL}" >&2; exit 1; }

# --- Запуск Api ----------------------------------------------------------------------------

cleanup() {
  local exit_code=$?
  if [[ "${KEEP_API}" != "1" && -n "${API_PID:-}" ]] && kill -0 "${API_PID}" 2>/dev/null; then
    kill "${API_PID}" 2>/dev/null || true
    wait "${API_PID}" 2>/dev/null || true
  elif [[ -n "${API_PID:-}" ]] && kill -0 "${API_PID}" 2>/dev/null; then
    echo "Api оставлен запущенным: http://127.0.0.1:${PORT} (PID ${API_PID}), лог ${LOG_FILE}"
  fi

  exit "${exit_code}"
}
trap cleanup EXIT INT TERM

echo "Запускаю Api на http://127.0.0.1:${PORT} (лог: ${LOG_FILE})…"

(
  cd "$(dirname "${API_DLL}")"
  ASPNETCORE_ENVIRONMENT=Testing \
  ASPNETCORE_URLS="http://127.0.0.1:${PORT}" \
  ConnectionStrings__Mayak="${CONNECTION_STRING}" \
  Jwt__Issuer="${JWT_ISSUER}" \
  Jwt__Audience="${JWT_AUDIENCE}" \
  Jwt__SigningKey="${JWT_SIGNING_KEY}" \
  Jwt__AccessTokenMinutes="${JWT_ACCESS_MINUTES}" \
  Observations__SweeperEnabled=false \
  Outbox__ApiDispatcherEnabled=false \
  RateLimiting__Enabled=false \
  exec dotnet Api.Host.dll --urls "http://127.0.0.1:${PORT}"
) >"${LOG_FILE}" 2>&1 &
API_PID=$!

READY=0
for _ in $(seq 1 90); do
  if curl -fsS "http://127.0.0.1:${PORT}/health" >/dev/null 2>&1; then
    READY=1
    break
  fi

  if ! kill -0 "${API_PID}" 2>/dev/null; then
    echo "Api завершился на старте. Лог ${LOG_FILE}:" >&2
    tail -n 60 "${LOG_FILE}" >&2 || true
    exit 1
  fi

  sleep 1
done

if [[ "${READY}" != "1" ]]; then
  echo "Api не ответил на /health за 90 секунд. Лог ${LOG_FILE}:" >&2
  tail -n 60 "${LOG_FILE}" >&2 || true
  exit 1
fi

# --- Токены seed-пользователей -------------------------------------------------------------

echo "Выпускаю access-токены dev-пользователей a1…a5…"

TOKEN_OUTPUT="$(
  MAYAK_CONNECTION="${CONNECTION_STRING}" \
  MAYAK_JWT_ISSUER="${JWT_ISSUER}" \
  MAYAK_JWT_AUDIENCE="${JWT_AUDIENCE}" \
  MAYAK_JWT_SIGNING_KEY="${JWT_SIGNING_KEY}" \
  dotnet "${TOKEN_DLL}"
)"

eval "${TOKEN_OUTPUT}"

# --- Schemathesis --------------------------------------------------------------------------

BASE_URL="http://127.0.0.1:${PORT}/api/v1"

# Schemathesis 3.x: --base-url, 4.x: --url (URL схемы при этом локальный файл).
BASE_OPTION="--url"
if ! schemathesis run --help 2>&1 | grep -q -- "--url"; then
  BASE_OPTION="--base-url"
fi

run_case() {
  local label="$1"
  shift

  echo
  echo "=== Schemathesis: ${label} (${BASE_URL}) ==="

  local args=(run "${SPEC_PATH}" --checks all "${BASE_OPTION}" "${BASE_URL}")
  if [[ $# -gt 0 ]]; then
    args+=("$@")
  fi

  schemathesis "${args[@]}"
}

case "${ROLE}" in
  anonymous) run_case "анонимно" ;;
  user|a1) run_case "user a1" --header "Authorization: Bearer ${MAYAK_TOKEN_A1}" ;;
  a2) run_case "user a2" --header "Authorization: Bearer ${MAYAK_TOKEN_A2}" ;;
  moderator|a3) run_case "moderator a3" --header "Authorization: Bearer ${MAYAK_TOKEN_A3}" ;;
  a4) run_case "moderator a4" --header "Authorization: Bearer ${MAYAK_TOKEN_A4}" ;;
  admin|a5) run_case "admin a5" --header "Authorization: Bearer ${MAYAK_TOKEN_A5}" ;;
  all)
    run_case "анонимно" || exit $?
    run_case "user a1" --header "Authorization: Bearer ${MAYAK_TOKEN_A1}" || exit $?
    run_case "moderator a3" --header "Authorization: Bearer ${MAYAK_TOKEN_A3}" || exit $?
    run_case "admin a5" --header "Authorization: Bearer ${MAYAK_TOKEN_A5}" || exit $?
    ;;
esac

echo
echo "Schemathesis завершён без падений."
