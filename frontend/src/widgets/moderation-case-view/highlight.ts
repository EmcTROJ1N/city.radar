export interface Segment {
  text: string;
  mark: boolean;
}

interface Normalized {
  value: string;
  map: number[];
}

/** Нижний регистр, любые пробельные последовательности — один пробел; map хранит исходные индексы. */
function normalize(text: string): Normalized {
  let value = '';
  const map: number[] = [];
  let space = false;

  for (let i = 0; i < text.length; i++) {
    const char = text[i]!;
    if (/\s/.test(char)) {
      if (value) space = true;
      continue;
    }
    if (space) {
      value += ' ';
      map.push(i - 1);
      space = false;
    }
    for (const lower of char.toLowerCase()) {
      value += lower;
      map.push(i);
    }
  }

  return { value, map };
}

function normalizeQuote(quote: string): string {
  return quote.trim().replace(/\s+/g, ' ').toLowerCase();
}

/**
 * Делит текст сообщения на сегменты, подсвечивая цитату-основание.
 * Поиск без учёта регистра и с нормализацией пробелов; не найдено — один сегмент без отметки.
 */
export function highlightEvidence(text: string, quote: string | null | undefined): Segment[] {
  const plain: Segment[] = [{ text, mark: false }];
  const needle = normalizeQuote(quote ?? '');
  if (!needle) return plain;

  const normalized = normalize(text);
  const index = normalized.value.indexOf(needle);
  if (index < 0) return plain;

  const start = normalized.map[index]!;
  const end = normalized.map[index + needle.length - 1]! + 1;

  const segments: Segment[] = [];
  if (start > 0) segments.push({ text: text.slice(0, start), mark: false });
  segments.push({ text: text.slice(start, end), mark: true });
  if (end < text.length) segments.push({ text: text.slice(end), mark: false });
  return segments;
}
