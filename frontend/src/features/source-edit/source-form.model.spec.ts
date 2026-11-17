import { describe, expect, it } from 'vitest';
import type { Source } from '@shared/api/types';
import {
  DEFAULT_TIMEOUT_SECONDS,
  buildPrefilter,
  emptySourceForm,
  formToCreate,
  formToStatusInput,
  formToUpdate,
  sourceToForm,
  splitKeywords,
  splitLines,
  splitTimeout,
  statusToForm,
  toTimeoutSeconds,
} from './source-form.model';

const SOURCE: Source = {
  id: 'src-1',
  name: 'Чат жильцов Калининского',
  sourceType: 'telegram_chat',
  chatId: '@kalininsky_water',
  timeoutSeconds: 21_600,
  confidenceThreshold: 0.4,
  prefilter: { keywords: ['вода', 'колонка'], regex: ['вод[аы]'] },
  userPrompt: 'Ищи все упоминания про подачу воды.',
  linkByText: false,
  isEnabled: true,
  state: 'ok',
  lastMessageAt: '2026-11-15T09:12:00Z',
  createdAt: '2026-11-01T08:00:00Z',
  statuses: [
    {
      id: 'st-1',
      code: 'ok',
      name: 'Вода есть',
      description: 'Подача воды идёт',
      color: 'ok',
      icon: 'drop',
      sortOrder: 1,
      statusDefinitionId: 'def-1',
    },
  ],
};

describe('таймаут источника', () => {
  it('раскладывает секунды в дни, часы или минуты', () => {
    expect(splitTimeout(21_600)).toEqual({ amount: 6, unit: 'hour' });
    expect(splitTimeout(86_400)).toEqual({ amount: 1, unit: 'day' });
    expect(splitTimeout(5_400)).toEqual({ amount: 90, unit: 'min' });
    expect(splitTimeout(30)).toEqual({ amount: 1, unit: 'min' });
  });

  it('переводит обратно в секунды и держит минимум 60', () => {
    expect(toTimeoutSeconds(6, 'hour')).toBe(21_600);
    expect(toTimeoutSeconds(1, 'day')).toBe(86_400);
    expect(toTimeoutSeconds(1, 'min')).toBe(60);
    expect(toTimeoutSeconds(0, 'min')).toBe(60);
    expect(toTimeoutSeconds(Number.NaN, 'hour')).toBe(60);
  });
});

describe('префильтр', () => {
  it('делит ключевые слова по запятой и выражения по строкам', () => {
    expect(splitKeywords(' вода, колонка , ,')).toEqual(['вода', 'колонка']);
    expect(splitLines('вод[аы]\n\n отключ')).toEqual(['вод[аы]', 'отключ']);
  });

  it('пустые значения дают null', () => {
    expect(buildPrefilter(' ', '\n')).toBeNull();
    expect(buildPrefilter('вода', '')).toEqual({ keywords: ['вода'] });
    expect(buildPrefilter('', 'вод[аы]')).toEqual({ regex: ['вод[аы]'] });
  });
});

describe('маппинг формы', () => {
  it('переносит источник в значения формы', () => {
    expect(sourceToForm(SOURCE)).toEqual({
      name: 'Чат жильцов Калининского',
      sourceType: 'telegram_chat',
      mode: 'bot',
      chatId: '@kalininsky_water',
      timeoutAmount: 6,
      timeoutUnit: 'hour',
      userPrompt: 'Ищи все упоминания про подачу воды.',
      linkByText: false,
      confidenceThreshold: 0.4,
      keywords: 'вода, колонка',
      regex: 'вод[аы]',
    });
  });

  it('по умолчанию включает linkByText и таймаут 6 ч', () => {
    const value = emptySourceForm();
    expect(value.linkByText).toBe(true);
    expect(toTimeoutSeconds(value.timeoutAmount, value.timeoutUnit)).toBe(
      DEFAULT_TIMEOUT_SECONDS,
    );
  });

  it('собирает SourceUpdate: обрезает пробелы и нормализует префильтр', () => {
    expect(
      formToUpdate({
        name: '  Чат  ',
        sourceType: 'max_chat',
        mode: 'bot',
        chatId: '  max-1  ',
        timeoutAmount: 2,
        timeoutUnit: 'day',
        userPrompt: '  Ищи воду  ',
        linkByText: true,
        confidenceThreshold: null,
        keywords: 'вода, ',
        regex: '',
      }),
    ).toEqual({
      name: 'Чат',
      sourceType: 'max_chat',
      chatId: 'max-1',
      timeoutSeconds: 172_800,
      confidenceThreshold: null,
      prefilter: { keywords: ['вода'] },
      userPrompt: 'Ищи воду',
      linkByText: true,
    });
  });

  it('передаёт способ чтения только у Telegram-источника', () => {
    const base = {
      ...emptySourceForm(),
      name: 'Чат',
      chatId: '@chat',
      userPrompt: 'Ищи воду',
    };

    expect(formToUpdate({ ...base, sourceType: 'telegram_chat', mode: 'userbot' }))
      .toMatchObject({ sourceType: 'telegram_chat', mode: 'userbot' });
    expect(formToUpdate({ ...base, sourceType: 'max_chat', mode: 'userbot' }))
      .not.toHaveProperty('mode');
  });

  it('добавляет статусы при создании и подставляет код из названия', () => {
    const value = {
      ...emptySourceForm(),
      name: 'Чат',
      chatId: '@chat',
      userPrompt: 'Ищи воду',
    };
    const body = formToCreate({
      ...value,
      statuses: [
        { code: '', name: 'Воды нет', description: '', color: 'none', icon: 'cross', statusDefinitionId: null },
        { code: 'ok', name: 'Вода есть', description: 'Есть', color: 'ok', icon: 'drop', statusDefinitionId: 'def-1' },
      ],
    });

    expect(body.statuses).toHaveLength(2);
    expect(body.statuses[0]).toMatchObject({ code: 'vody_net', name: 'Воды нет', sortOrder: 1 });
    expect(body.statuses[0]).not.toHaveProperty('description');
    expect(body.statuses[1]).toEqual({
      code: 'ok',
      name: 'Вода есть',
      description: 'Есть',
      color: 'ok',
      icon: 'drop',
      sortOrder: 2,
      statusDefinitionId: 'def-1',
    });
  });

  it('переносит статус из API в значения формы и обратно', () => {
    const value = statusToForm(SOURCE.statuses[0]!);
    expect(value).toEqual({
      code: 'ok',
      name: 'Вода есть',
      description: 'Подача воды идёт',
      color: 'ok',
      icon: 'drop',
      statusDefinitionId: 'def-1',
    });
    expect(formToStatusInput(value, 3)).toMatchObject({ code: 'ok', sortOrder: 3 });
  });
});
