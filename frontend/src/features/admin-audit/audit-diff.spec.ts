import { describe, expect, it } from 'vitest';
import { changedKeys, jsonLines, sameValue } from './audit-diff';

describe('changedKeys', () => {
  it('находит изменённые, добавленные и удалённые ключи верхнего уровня', () => {
    const before = { name: 'Старое', timeout: 240, removed: true };
    const after = { name: 'Новое', timeout: 240, added: 'да' };

    expect(changedKeys(before, after)).toEqual(['added', 'name', 'removed']);
  });

  it('считает вложенные объекты равными независимо от порядка ключей', () => {
    expect(sameValue({ a: 1, b: { c: 2, d: 3 } }, { b: { d: 3, c: 2 }, a: 1 })).toBe(true);
    expect(changedKeys({ nested: { a: 1 } }, { nested: { a: 2 } })).toEqual(['nested']);
  });

  it('поддерживает отсутствующую сторону и пустые значения', () => {
    expect(changedKeys(null, { a: 1 })).toEqual(['a']);
    expect(changedKeys({ a: 1 }, null)).toEqual(['a']);
    expect(changedKeys(null, null)).toEqual([]);
  });
});

describe('jsonLines', () => {
  it('отмечает строки только изменённых ключей верхнего уровня', () => {
    const lines = jsonLines({ name: 'Новое', timeout: 240 }, ['name']);

    expect(lines.find((line) => line.text.includes('"name"'))?.changed).toBe(true);
    expect(lines.find((line) => line.text.includes('"timeout"'))?.changed).toBe(false);
  });

  it('не отмечает вложенные ключи', () => {
    const lines = jsonLines({ nested: { inner: 1 } }, ['nested']);

    expect(lines.filter((line) => line.changed)).toHaveLength(1);
    expect(lines.find((line) => line.text.includes('"inner"'))?.changed).toBe(false);
  });

  it('возвращает пустой список для null', () => {
    expect(jsonLines(null, ['x'])).toEqual([]);
  });
});
