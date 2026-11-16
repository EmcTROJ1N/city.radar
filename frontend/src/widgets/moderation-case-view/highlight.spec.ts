import { describe, expect, it } from 'vitest';
import { highlightEvidence } from './highlight';

const TEXT = 'Воды нет с утра, воду обещают к вечеру.';

describe('highlightEvidence', () => {
  it('выделяет цитату и сохраняет текст вокруг', () => {
    expect(highlightEvidence(TEXT, 'Воды нет')).toEqual([
      { text: 'Воды нет', mark: true },
      { text: ' с утра, воду обещают к вечеру.', mark: false },
    ]);
  });

  it('не зависит от регистра', () => {
    const segments = highlightEvidence(TEXT, 'ВОДУ ОБЕЩАЮТ');
    expect(segments.filter((segment) => segment.mark)).toHaveLength(1);
    expect(segments.find((segment) => segment.mark)?.text).toBe('воду обещают');
  });

  it('нормализует пробелы и переносы строк', () => {
    const text = 'Воды\n\nнет   с утра';
    expect(highlightEvidence(text, 'воды нет')).toEqual([
      { text: 'Воды\n\nнет', mark: true },
      { text: '   с утра', mark: false },
    ]);
  });

  it('если цитата не найдена — один сегмент без отметки', () => {
    expect(highlightEvidence(TEXT, 'воды по графику')).toEqual([{ text: TEXT, mark: false }]);
  });

  it('пустая цитата не даёт подсветки', () => {
    expect(highlightEvidence(TEXT, '   ')).toEqual([{ text: TEXT, mark: false }]);
    expect(highlightEvidence(TEXT, null)).toEqual([{ text: TEXT, mark: false }]);
  });
});
