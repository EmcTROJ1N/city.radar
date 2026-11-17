import type {
  Freshness,
  ModerationCaseKind,
  ModerationCaseState,
  ObservationSourceType,
  SourceState,
  SourceType,
  StatusColor,
  TelegramMode,
} from '../api/types';

export const SOURCE_TYPE_LABEL: Record<SourceType, string> = {
  telegram_chat: 'Telegram-чат',
  max_chat: 'MAX-чат',
  site: 'Сайт',
};

export const TELEGRAM_MODE_LABEL: Record<TelegramMode, string> = {
  bot: 'Telegram-бот (Bot API)',
  userbot: 'Аккаунт Telegram (MTProto)',
};

export const TELEGRAM_MODE_SHORT: Record<TelegramMode, string> = {
  bot: 'Бот',
  userbot: 'MTProto',
};

export const TELEGRAM_MODE_HINT: Record<TelegramMode, string> = {
  bot: 'Бот состоит в чате и читает свои чаты. Подключение — токен от @BotFather.',
  userbot:
    'Аккаунт читает сторонние группы от своего имени. Подключение — api_id, api_hash и сохранённая сессия.',
};

export const OBSERVATION_SOURCE_LABEL: Record<ObservationSourceType, string> = {
  manual: 'Житель',
  moderator: 'Модератор',
  telegram: 'Telegram',
  max: 'MAX',
};

export const SOURCE_STATE_LABEL: Record<SourceState, string> = {
  unknown: 'Нет данных',
  ok: 'Работает',
  silent: 'Нет сообщений',
  flood_wait: 'FloodWait',
  auth_error: 'Ошибка авторизации',
  error: 'Ошибка',
};

export const CASE_KIND_LABEL: Record<ModerationCaseKind, string> = {
  low_confidence: 'Низкая достоверность',
  invalid_status: 'Некорректный статус',
  object_not_found: 'Фиксация не найдена',
  invalid_response: 'Некорректный ответ',
  anomaly: 'Аномалия',
  complaint: 'Жалоба',
};

export const CASE_STATE_LABEL: Record<ModerationCaseState, string> = {
  open: 'Открыт',
  approved: 'Принят',
  rejected: 'Отклонён',
  dismissed: 'Закрыт',
};

export const FRESHNESS_LABEL: Record<Freshness, string> = {
  fresh: 'Актуально',
  aging: 'Скоро устареет',
  stale: 'Устарело',
};

export const STATUS_COLOR_LABEL: Record<StatusColor, string> = {
  ok: 'Зелёный',
  limited: 'Жёлтый',
  none: 'Красный',
  queue: 'Фиолетовый',
  schedule: 'Синий',
  stale: 'Серый',
};
