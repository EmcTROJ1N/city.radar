#!/bin/sh
# Резервное копирование PostgreSQL: pg_dump в том с ротацией.
#
# Простой sidecar-цикл вместо cron: интервал и срок хранения задаются переменными окружения.
# Формат custom (-Fc) восстанавливается pg_restore и позволяет выборочное восстановление.
# Для RPO ≤ 15 минут нужна непрерывная WAL-архивация — это TODO инфраструктуры:
# ежедневный дамп её не заменяет.
set -eu

interval="${BACKUP_INTERVAL_SECONDS:-86400}"
retention_days="${BACKUP_RETENTION_DAYS:-14}"
backup_dir="${BACKUP_DIR:-/backups}"
prefix="${BACKUP_FILE_PREFIX:-mayak}"

mkdir -p "$backup_dir"

log() {
    echo "backup: $(date -u '+%Y-%m-%dT%H:%M:%SZ') $*"
}

if [ -z "${PGHOST:-}" ] || [ -z "${PGDATABASE:-}" ]; then
    echo "backup: не заданы PGHOST/PGDATABASE" >&2
    exit 1
fi

while :; do
    timestamp=$(date -u '+%Y%m%dT%H%M%SZ')
    dump_file="$backup_dir/$prefix-$timestamp.dump"

    log "pg_dump -> $dump_file"
    # -Z 6: сжатие внутри custom-формата; --no-owner/--no-privileges — дамп переносим между кластерами.
    if pg_dump --format=custom --compress=6 --no-owner --no-privileges --file "$dump_file"; then
        # Маркер успеха: используется healthcheck-ом sidecar-а и человеком при разборе инцидента.
        date -u '+%s' > "$backup_dir/.last-success"
        log "готово, ротация старше ${retention_days} дней"
        find "$backup_dir" -maxdepth 1 -type f -name "$prefix-*.dump" -mtime "+$retention_days" -print -delete || true
    else
        # Неполный дамп хуже отсутствующего: его легко принять за валидный.
        log "ОШИБКА pg_dump, файл удалён"
        rm -f "$dump_file"
    fi

    log "следующий запуск через ${interval} с"
    sleep "$interval"
done
