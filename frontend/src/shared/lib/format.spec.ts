import { describe, expect, it } from 'vitest';
import { agoIso, dateTime, durationSec } from './format';

describe('agoIso', () => {
  const now = Date.parse('2026-11-15T12:00:00Z');

  it('отдаёт «только что» для свежей даты', () => {
    expect(agoIso('2026-11-15T11:59:40Z', now)).toBe('только что');
  });

  it('считает минуты, часы и дни', () => {
    expect(agoIso('2026-11-15T11:55:00Z', now)).toBe('5 мин назад');
    expect(agoIso('2026-11-15T10:00:00Z', now)).toBe('2 ч назад');
    expect(agoIso('2026-11-12T12:00:00Z', now)).toBe('3 дн назад');
  });

  it('на некорректной дате отдаёт пустую строку', () => {
    expect(agoIso('не дата', now)).toBe('');
  });
});

describe('dateTime', () => {
  it('форматирует as dd.MM.yyyy HH:mm в локальном времени', () => {
    expect(dateTime(new Date(2026, 0, 5, 7, 3).toISOString())).toBe('05.01.2026 07:03');
  });

  it('на некорректной дате отдаёт пустую строку', () => {
    expect(dateTime('не дата')).toBe('');
  });
});

describe('durationSec', () => {
  it('переводит секунды в минуты, часы и дни', () => {
    expect(durationSec(45 * 60)).toBe('45 мин');
    expect(durationSec(6 * 3600)).toBe('6 ч');
    expect(durationSec((24 + 2) * 3600)).toBe('1 д 2 ч');
  });

  it('добавляет остаток минут и часов', () => {
    expect(durationSec(90 * 60)).toBe('1 ч 30 мин');
    expect(durationSec(30)).toBe('30 с');
  });
});
