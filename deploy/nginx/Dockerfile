# nginx с openssl: сертификат-заглушка генерируется при первом старте контейнера.
FROM nginx:1.27-alpine

# openssl нужен только для самоподписанного сертификата (TODO заменить на реальный).
RUN apk add --no-cache openssl

COPY nginx.conf /etc/nginx/nginx.conf
COPY snippets/ /etc/nginx/snippets/
COPY 10-self-signed-cert.sh /docker-entrypoint.d/10-self-signed-cert.sh
RUN chmod 0555 /docker-entrypoint.d/10-self-signed-cert.sh

# Проверка конфигурации при сборке: синтаксическая ошибка не должна доезжать до деплоя.
# Временный сертификат нужен только для `nginx -t` и в образ не попадает.
RUN mkdir -p /etc/nginx/certs \
    && openssl req -x509 -nodes -newkey rsa:2048 -days 1 \
        -keyout /etc/nginx/certs/server.key \
        -out /etc/nginx/certs/server.crt \
        -subj "/CN=config-check" >/dev/null 2>&1 \
    && nginx -t \
    && rm -rf /etc/nginx/certs

EXPOSE 80 443
