export type ParsedSchema =
  | { ok: true; value: Record<string, unknown> }
  | { ok: false; error: string };

export const EXAMPLE_SCHEMA =
  '{"type":"object","properties":{"note":{"type":"string","title":"Примечание"}}}';

/** Пустое поле — пустая схема; иначе это должен быть JSON-объект. */
export function parseAttributesSchema(text: string): ParsedSchema {
  const trimmed = text.trim();
  if (!trimmed) return { ok: true, value: {} };

  let parsed: unknown;
  try {
    parsed = JSON.parse(trimmed);
  } catch {
    return { ok: false, error: 'Некорректный JSON — проверьте кавычки, запятые и скобки' };
  }

  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    return { ok: false, error: 'Схема атрибутов должна быть JSON-объектом, например { "type": "object" }' };
  }
  return { ok: true, value: parsed as Record<string, unknown> };
}
