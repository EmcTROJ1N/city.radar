#!/bin/sh
# Самоподписанный сертификат-заглушка для TLS-терминации.
#
# TODO: заменить на сертификат Let's Encrypt или корпоративный: положить server.crt/server.key
# в том proxy-certs (или смонтировать секрет) и не давать этому скрипту их перегенерировать.
# Скрипт выполняется официальным entrypoint nginx из /docker-entrypoint.d/ до старта nginx.
set -eu

cert_dir="${TLS_CERT_DIR:-/etc/nginx/certs}"
server_name="${TLS_SERVER_NAME:-localhost}"
days="${TLS_SELF_SIGNED_DAYS:-30}"

if [ -s "$cert_dir/server.crt" ] && [ -s "$cert_dir/server.key" ]; then
    echo "nginx: использую существующий сертификат из $cert_dir"
    exit 0
fi

echo "nginx: генерирую самоподписанный сертификат-заглушку для $server_name (TODO: реальный TLS)"
mkdir -p "$cert_dir"

openssl req -x509 -nodes -newkey rsa:2048 -days "$days" \
    -keyout "$cert_dir/server.key" \
    -out "$cert_dir/server.crt" \
    -subj "/CN=$server_name" \
    -addext "subjectAltName=DNS:$server_name,IP:127.0.0.1" \
    >/dev/null 2>&1

chmod 0600 "$cert_dir/server.key"
chmod 0644 "$cert_dir/server.crt"
