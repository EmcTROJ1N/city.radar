import { describe, expect, it } from 'vitest';
import { parseAttributesSchema } from './attributes-schema';

describe('parseAttributesSchema', () => {
  it('пустое поле — пустая схема', () => {
    expect(parseAttributesSchema('   ')).toEqual({ ok: true, value: {} });
  });

  it('разбирает JSON-объект', () => {
    const result = parseAttributesSchema('{"type":"object","properties":{}}');
    expect(result).toEqual({ ok: true, value: { type: 'object', properties: {} } });
  });

  it('сообщает о синтаксической ошибке', () => {
    const result = parseAttributesSchema('{"type": }');
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.error).toContain('Некорректный JSON');
  });

  it('не принимает массив вместо объекта', () => {
    const result = parseAttributesSchema('[1, 2]');
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.error).toContain('JSON-объектом');
  });

  it('не принимает null и примитивы', () => {
    for (const text of ['null', '42', '"schema"', 'true']) {
      expect(parseAttributesSchema(text).ok).toBe(false);
    }
  });
});
