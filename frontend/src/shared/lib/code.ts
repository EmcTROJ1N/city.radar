const TRANSLIT: Record<string, string> = {
  а: 'a', б: 'b', в: 'v', г: 'g', д: 'd', е: 'e', ё: 'e', ж: 'zh', з: 'z', и: 'i', й: 'y',
  к: 'k', л: 'l', м: 'm', н: 'n', о: 'o', п: 'p', р: 'r', с: 's', т: 't', у: 'u', ф: 'f',
  х: 'kh', ц: 'ts', ч: 'ch', ш: 'sh', щ: 'shch', ъ: '', ы: 'y', ь: '', э: 'e', ю: 'yu', я: 'ya',
};

/** Латинский код из русского названия: «Воды нет» → «vody_net». */
export function slugCode(name: string): string {
  let result = '';
  for (const ch of name.toLowerCase()) {
    if (Object.hasOwn(TRANSLIT, ch)) result += TRANSLIT[ch];
    else result += /[a-z0-9]/.test(ch) ? ch : '_';
  }
  return result.replace(/_+/g, '_').replace(/^_+|_+$/g, '');
}
