import { describe, expect, it } from 'vitest';
import { slugCode } from './code';

describe('slugCode', () => {
  it('транслитерирует русское название', () => {
    expect(slugCode('Воды нет')).toBe('vody_net');
    expect(slugCode('АВАРИЯ')).toBe('avariya');
  });

  it('передаёт сложные буквы', () => {
    expect(slugCode('Щёлочь 50%')).toBe('shcheloch_50');
    expect(slugCode('Хлеб')).toBe('khleb');
    expect(slugCode('Цистерна')).toBe('tsisterna');
  });

  it('убирает повторные и крайние подчёркивания', () => {
    expect(slugCode('  Вода — нет  ')).toBe('voda_net');
    expect(slugCode('...')).toBe('');
  });

  it('оставляет только латиницу, цифры и подчёркивания', () => {
    expect(slugCode('Пункт №3 (резерв)')).toMatch(/^[a-z0-9_]+$/);
  });
});
