export interface JsonLine {
  text: string;
  changed: boolean;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

export function sameValue(a: unknown, b: unknown): boolean {
  if (Object.is(a, b)) return true;
  if (Array.isArray(a) || Array.isArray(b)) {
    if (!Array.isArray(a) || !Array.isArray(b) || a.length !== b.length) return false;
    return a.every((item, index) => sameValue(item, b[index]));
  }
  if (isRecord(a) && isRecord(b)) {
    const aKeys = Object.keys(a);
    const bKeys = Object.keys(b);
    if (aKeys.length !== bKeys.length) return false;
    return aKeys.every((key) => Object.hasOwn(b, key) && sameValue(a[key], b[key]));
  }
  return false;
}

/** Ключи верхнего уровня, значения которых отличаются (включая добавленные и удалённые). */
export function changedKeys(
  before: Record<string, unknown> | null | undefined,
  after: Record<string, unknown> | null | undefined,
): string[] {
  const left = before ?? {};
  const right = after ?? {};
  const keys = new Set([...Object.keys(left), ...Object.keys(right)]);
  return [...keys].filter((key) => !sameValue(left[key], right[key])).sort();
}

/** Строки JSON с отметкой изменённых ключей верхнего уровня. */
export function jsonLines(value: unknown, changed: readonly string[]): JsonLine[] {
  if (value === null || value === undefined) return [];
  const raw = JSON.stringify(value, null, 2);
  if (!raw) return [];
  const diff = new Set(changed);
  return raw.split('\n').map((text) => {
    const match = /^ {2}"((?:[^"\\]|\\.)*)":/.exec(text);
    return { text, changed: match ? diff.has(decodeKey(match[1] ?? '')) : false };
  });
}

function decodeKey(raw: string): string {
  try {
    return JSON.parse(`"${raw}"`) as string;
  } catch {
    return raw;
  }
}
