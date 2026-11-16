# Городские ресурсы ДНР — фронтенд

Клиент портала мониторинга городских ресурсов: публичная карта доступности воды, источники данных,
фиксации на карте, каталог, модерация и администрирование. Работает на контракте `api/openapi.yaml`.

Стек: Angular 21 (standalone, signals, zoneless), MapLibre GL JS, Feature-Sliced Design, Vitest.

## Запуск

Нужен бэкенд: Postgres и Redis через `docker compose` (см. `deploy/`), затем `Api.Host` на `:5178`.

```bash
npm install
npm start                 # http://localhost:4200, /api/v1 проксируется на бэкенд :5178
```

### Вход в dev

На странице «Вход» есть блок «Вход для разработки» (только при `devAuth`; в production-сборке его
нет). Логин и пароль отправляются на `POST /auth/dev-login` бэкенда, который доступен только в
Development-окружении.

Логин — MAX ID пользователя из базы (например, `900000005`) или `admin` / `moderator` / `user`;
пароль — `DevAuth:Password` из `appsettings.Development.json`. Сессия после перезагрузки
восстанавливается штатно: `POST /auth/refresh` по httpOnly-cookie, затем `GET /me`.

## Структура (FSD)

```
src/
  app/       — оболочка, маршруты, провайдеры, service worker
  pages/     — страницы-маршруты (карта, источники, каталог, модерация, админ)
  widgets/   — крупные блоки интерфейса (карточка объекта, фильтры карты)
  features/  — пользовательские сценарии (редактирование, жалоба, модерация)
  entities/  — сущности и API-сервисы (источник, фиксация, наблюдение, пользователь)
  shared/    — api, ui, lib, styles, map-adapter — переиспользуемая основа
```

Слой может импортировать только нижележащие слои: `app > pages > widgets > features > entities > shared`.
Слайсы одного слоя не импортируют друг друга. Границы проверяет steiger.

## Команды

```bash
npm run verify   # typecheck + тесты типов + тесты скриптов + production-сборка + тесты + линтер
npm run lint     # steiger: границы слоёв и слайсов FSD
npm run types    # перегенерировать TypeScript-типы из api/openapi.yaml
npm run map-style  # перегенерировать стили подложки в public/map-style
npm test         # vitest
npm run build    # production-сборка с service worker
```

## Подложка карты

Подложка — OpenFreeMap (OpenMapTiles, данные OpenStreetMap); Яндекс не используется. Готовые стили
лежат в `public/map-style/light.json` и `dark.json` и коммитятся; их собирает генератор
`scripts/build-map-style.mjs` из стилей `https://tiles.openfreemap.org/styles/positron` (светлый) и
`/dark` (тёмный):

```bash
npm run map-style       # скачать стили и перезаписать public/map-style/*.json
npm run test:scripts    # тесты трансформации (node --test)
```

Трансформация (`scripts/map-style-transform.mjs`) оставляет в подписях только русский `name:ru`
(нет русского — нет подписи; `housenumber` не трогает, слои с номерами дорог `ref` и слои
границ/стран удаляет) и подставляет русскую атрибуцию векторного источника. Тема выбирается по
`data-theme` на `<html>`: при смене темы карта
переключает стиль и заново добавляет свои слои. Переход на собственные тайлы — сменить `url`
источника и `glyphs` в генераторе.

## Окружение

Переменные задаются в `src/environments/environment.ts` (dev) и `environment.prod.ts` (prod):

- `apiBase` — базовый путь API, по умолчанию `/api/v1`;
- `basemapStyle` — стили подложки MapLibre по теме: `{ light, dark }`, по умолчанию
  `/map-style/light.json` и `/map-style/dark.json`. Обе строки пустые — карта без подложки, только фон;
- `pollIntervalMs` — период опроса `/map/features` на публичной карте;
- `devAuth` — показывает блок входа для разработки (только dev);
- `mapCenter`, `mapZoom` — запасной начальный вид карты, если геозоны не найдены;
- `mapCoverageBbox` — область, в которой публичная карта ищет геозоны для стартового
  вида: `[minLon, minLat, maxLon, maxLat]`. Найденные зоны задают первый масштаб,
  поэтому работа начинается рядом с ними, а не в центре города по умолчанию.

## Геолокация

На публичной карте и в редакторе фиксаций браузер запрашивает доступ к местоположению:
текущая позиция показывается синей точкой, кнопка с прицелом в правом нижнем углу карты
центрирует вид на ней. Как и любой Geolocation API, она работает только в защищённом
контексте — по HTTPS или на `localhost`. В production-прокси `deploy/nginx` для этого
разрешён `Permissions-Policy: geolocation=(self)`.

## PWA

Production-сборка регистрирует service worker (`ngsw-config.json`): оболочка кэшируется заранее,
последнее состояние карты и справочники каталога — на 1 день по стратегии freshness, таймаут 5 с.
Запросы авторизации, `/me`, админки и POST не кэшируются. При потере сети в шапке появляется
индикатор «Нет сети», а при выходе новой версии — плашка «Доступна новая версия».
