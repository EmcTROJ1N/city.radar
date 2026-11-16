import type { CursorPage } from './api-client';

/** Выгружает все страницы курсорной пагинации, пока не кончится курсор или не наберётся `max`. */
export async function collectAll<T>(
  load: (cursor: string | null) => Promise<CursorPage<T>>,
  max = 1000,
): Promise<T[]> {
  const items: T[] = [];
  const seen = new Set<string>();
  let cursor: string | null = null;
  while (items.length < max) {
    const page = await load(cursor);
    items.push(...page.items);
    cursor = page.nextCursor;
    if (cursor === null || seen.has(cursor)) break;
    seen.add(cursor);
  }
  return items.slice(0, max);
}
