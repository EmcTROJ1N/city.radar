import type {
  Source,
  SourceCreate,
  SourceStatus,
  SourceStatusInput,
  SourceType,
  SourceUpdate,
  StatusColor,
  TelegramMode,
} from '@shared/api/types';
import {
  SOURCE_TYPE_LABEL,
  STATUS_COLOR_LABEL,
  TELEGRAM_MODE_LABEL,
} from '@shared/lib/labels';
import { slugCode } from '@shared/lib/code';
import { normalizeIcon, type IconName } from '@shared/ui/icon';

export const DEFAULT_TIMEOUT_SECONDS = 21_600;
export const MIN_TIMEOUT_SECONDS = 60;

export type SourcePrefilter = NonNullable<SourceUpdate['prefilter']>;

export const TIMEOUT_UNITS = [
  { value: 'min', label: 'мин', seconds: 60 },
  { value: 'hour', label: 'ч', seconds: 3_600 },
  { value: 'day', label: 'д', seconds: 86_400 },
] as const;

export type TimeoutUnit = (typeof TIMEOUT_UNITS)[number]['value'];

export const SOURCE_TYPE_OPTIONS: { value: SourceType; label: string }[] = [
  { value: 'telegram_chat', label: SOURCE_TYPE_LABEL.telegram_chat },
  { value: 'max_chat', label: SOURCE_TYPE_LABEL.max_chat },
];

/** Способы чтения Telegram: бот для своих чатов, MTProto для сторонних групп. */
export const TELEGRAM_MODE_OPTIONS: { value: TelegramMode; label: string }[] = [
  { value: 'bot', label: TELEGRAM_MODE_LABEL.bot },
  { value: 'userbot', label: TELEGRAM_MODE_LABEL.userbot },
];

/** Фильтр списка знает и системный тип: «Сайт» создаётся только вместе с категорией. */
export const SOURCE_TYPE_FILTER_OPTIONS: { value: SourceType; label: string }[] = [
  ...SOURCE_TYPE_OPTIONS,
  { value: 'site', label: SOURCE_TYPE_LABEL.site },
];

export const STATUS_COLOR_OPTIONS: { value: StatusColor; label: string }[] = (
  ['ok', 'limited', 'none', 'queue', 'schedule', 'stale'] as const
).map((value) => ({ value, label: STATUS_COLOR_LABEL[value] }));

export const STATUS_ICON_OPTIONS: { value: IconName; label: string }[] = [
  { value: 'drop', label: 'Капля' },
  { value: 'cross', label: 'Крест' },
  { value: 'check', label: 'Галочка' },
  { value: 'clock', label: 'Часы' },
  { value: 'alert', label: 'Внимание' },
  { value: 'question', label: 'Вопрос' },
];

export interface SourceFormValue {
  name: string;
  sourceType: SourceType;
  mode: TelegramMode;
  chatId: string;
  timeoutAmount: number;
  timeoutUnit: TimeoutUnit;
  userPrompt: string;
  linkByText: boolean;
  confidenceThreshold: number | null;
  keywords: string;
  regex: string;
}

export interface SourceStatusFormValue {
  code: string;
  name: string;
  description: string;
  color: StatusColor;
  icon: IconName;
  statusDefinitionId: string | null;
}

/** Секунды → удобная пара «число + единица»: 21 600 → 6 ч, 90 000 → 25 ч. */
export function splitTimeout(seconds: number): { amount: number; unit: TimeoutUnit } {
  const total = Math.max(MIN_TIMEOUT_SECONDS, Math.round(seconds));
  if (total % 86_400 === 0) return { amount: total / 86_400, unit: 'day' };
  if (total % 3_600 === 0) return { amount: total / 3_600, unit: 'hour' };
  return { amount: Math.max(1, Math.round(total / 60)), unit: 'min' };
}

export function toTimeoutSeconds(amount: number, unit: TimeoutUnit): number {
  const factor = TIMEOUT_UNITS.find((item) => item.value === unit)?.seconds ?? 60;
  if (!Number.isFinite(amount)) return MIN_TIMEOUT_SECONDS;
  return Math.max(MIN_TIMEOUT_SECONDS, Math.round(amount * factor));
}

/** «вода, колонка» → ['вода', 'колонка']; пустые значения отбрасываются. */
export function splitKeywords(raw: string): string[] {
  return raw
    .split(',')
    .map((item) => item.trim())
    .filter(Boolean);
}

/** Одно регулярное выражение на строку. */
export function splitLines(raw: string): string[] {
  return raw
    .split('\n')
    .map((item) => item.trim())
    .filter(Boolean);
}

export function buildPrefilter(keywords: string, regex: string): SourcePrefilter | null {
  const values: SourcePrefilter = {};
  const keywordList = splitKeywords(keywords);
  const regexList = splitLines(regex);
  if (keywordList.length) values.keywords = keywordList;
  if (regexList.length) values.regex = regexList;
  return keywordList.length || regexList.length ? values : null;
}

export function emptySourceForm(): SourceFormValue {
  const timeout = splitTimeout(DEFAULT_TIMEOUT_SECONDS);
  return {
    name: '',
    sourceType: 'telegram_chat',
    mode: 'bot',
    chatId: '',
    timeoutAmount: timeout.amount,
    timeoutUnit: timeout.unit,
    userPrompt: '',
    linkByText: true,
    confidenceThreshold: null,
    keywords: '',
    regex: '',
  };
}

export function emptyStatus(): SourceStatusFormValue {
  return {
    code: '',
    name: '',
    description: '',
    color: 'ok',
    icon: 'check',
    statusDefinitionId: null,
  };
}

export function sourceToForm(source: Source): SourceFormValue {
  const timeout = splitTimeout(source.timeoutSeconds);
  return {
    name: source.name,
    sourceType: source.sourceType,
    mode: source.mode ?? 'bot',
    chatId: source.chatId,
    timeoutAmount: timeout.amount,
    timeoutUnit: timeout.unit,
    userPrompt: source.userPrompt,
    linkByText: source.linkByText,
    confidenceThreshold: source.confidenceThreshold ?? null,
    keywords: (source.prefilter?.keywords ?? []).join(', '),
    regex: (source.prefilter?.regex ?? []).join('\n'),
  };
}

export function statusToForm(status: SourceStatus): SourceStatusFormValue {
  return {
    code: status.code,
    name: status.name,
    description: status.description ?? '',
    color: status.color,
    icon: normalizeIcon(status.icon),
    statusDefinitionId: status.statusDefinitionId ?? null,
  };
}

export function formToUpdate(value: SourceFormValue): SourceUpdate {
  const body: SourceUpdate = {
    name: value.name.trim(),
    sourceType: value.sourceType,
    chatId: value.chatId.trim(),
    timeoutSeconds: toTimeoutSeconds(value.timeoutAmount, value.timeoutUnit),
    confidenceThreshold: value.confidenceThreshold,
    prefilter: buildPrefilter(value.keywords, value.regex),
    userPrompt: value.userPrompt.trim(),
    linkByText: value.linkByText,
  };

  // Режим относится только к Telegram; у MAX и системного «Сайта» поля нет.
  if (value.sourceType === 'telegram_chat') body.mode = value.mode;

  return body;
}

export function formToStatusInput(
  value: SourceStatusFormValue,
  sortOrder: number,
): SourceStatusInput {
  const description = value.description.trim();
  return {
    code: value.code.trim() || slugCode(value.name),
    name: value.name.trim(),
    ...(description ? { description } : {}),
    color: value.color,
    icon: value.icon,
    sortOrder,
    statusDefinitionId: value.statusDefinitionId,
  };
}

export function formToCreate(
  value: SourceFormValue & { statuses: SourceStatusFormValue[] },
): SourceCreate {
  return {
    ...formToUpdate(value),
    statuses: value.statuses.map((status, index) => formToStatusInput(status, index + 1)),
  };
}

