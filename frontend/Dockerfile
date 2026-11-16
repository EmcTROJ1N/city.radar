# syntax=docker/dockerfile:1.7
#
# Фронтенд для локального стека (compose.yaml): Angular собирается в Node и раздаётся nginx,
# который заодно проксирует /api/ в контейнер api. Сборка Development: без service worker,
# чтобы браузер не показывал закэшированную версию после пересборки.
#
#   docker build -t fmd-water-frontend frontend

FROM node:22-alpine AS build
WORKDIR /app

# Сначала только манифесты — слой npm ci кэшируется и переживает правки исходников.
COPY package.json package-lock.json ./
RUN npm ci

COPY . .
RUN npm run build -- --configuration development

FROM nginx:1.27-alpine AS runtime

COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/frontend/browser /usr/share/nginx/html

EXPOSE 80

HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=5 \
    CMD wget -q -O /dev/null http://127.0.0.1/healthz || exit 1
