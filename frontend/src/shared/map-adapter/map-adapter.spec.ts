import { describe, expect, it } from 'vitest';
import { normalizeIcon } from '../ui/icon';
import { bboxParam } from './map-adapter';

describe('bboxParam', () => {
  it('пишет четыре координаты через запятую с пятью знаками', () => {
    expect(bboxParam({ minLon: 37.8, minLat: 48, maxLon: 38.123456, maxLat: 48.2 })).toBe(
      '37.80000,48.00000,38.12346,48.20000',
    );
  });

  it('округляет отрицательные координаты', () => {
    expect(bboxParam({ minLon: -0.000004, minLat: -37.555555, maxLon: 0, maxLat: 0 })).toBe(
      '-0.00000,-37.55555,0.00000,0.00000',
    );
  });
});

describe('normalizeIcon', () => {
  it('оставляет известные имена иконок', () => {
    expect(normalizeIcon('drop')).toBe('drop');
    expect(normalizeIcon('cross')).toBe('cross');
    expect(normalizeIcon('check')).toBe('check');
  });

  it('приводит регистр и пробелы', () => {
    expect(normalizeIcon(' Clock ')).toBe('clock');
  });

  it('неизвестное имя заменяет на вопросительный знак', () => {
    expect(normalizeIcon('water')).toBe('question');
    expect(normalizeIcon('')).toBe('question');
    expect(normalizeIcon(null)).toBe('question');
    expect(normalizeIcon(undefined)).toBe('question');
  });
});
