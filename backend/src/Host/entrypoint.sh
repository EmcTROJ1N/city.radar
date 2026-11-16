#!/bin/sh
# Общий entrypoint Api.Host и IngestionWorker.Host.
#
# Docker secrets приходят файлами. Приложение читает только обычную конфигурацию .NET, поэтому
# секреты, для которых нет отдельного обработчика (JWT-ключ, строка подключения), читаются здесь
# и экспортируются в переменные конфигурации до запуска процесса. Мастер-ключ шифрования секретов
# отдельного шага не требует: BuildingBlocks.Security сам читает файл из MAYAK_MASTER_KEY_FILE.
#
# Значения секретов не логируются. Имена файлов — часть контракта compose-файла.
set -eu

# read_secret_to_env <переменная с путём к файлу> <переменная конфигурации .NET>
read_secret_to_env() {
    file_variable="$1"
    target_variable="$2"

    eval "secret_path=\${$file_variable:-}"
    if [ -z "$secret_path" ]; then
        return 0
    fi

    if [ ! -r "$secret_path" ]; then
        echo "entrypoint: файл секрета из $file_variable недоступен: $secret_path" >&2
        exit 1
    fi

    # $(cat ...) обрезает завершающие переводы строки — они не часть значения секрета.
    secret_value=$(cat "$secret_path")
    if [ -z "$secret_value" ]; then
        echo "entrypoint: файл секрета из $file_variable пуст: $secret_path" >&2
        exit 1
    fi

    export "$target_variable=$secret_value"
    unset secret_value
}

read_secret_to_env JWT_SIGNING_KEY_FILE Jwt__SigningKey
read_secret_to_env MAYAK_CONNECTION_STRING_FILE ConnectionStrings__Mayak

exec "$@"
