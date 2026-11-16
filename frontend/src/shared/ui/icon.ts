import { Component, computed, input } from '@angular/core';

const ICONS = {
  map: ['M9 4 3 6v14l6-2 6 2 6-2V4l-6 2-6-2z', 'M9 4v14', 'M15 6v14'],
  layers: ['M12 3 3 8l9 5 9-5-9-5z', 'M3 13l9 5 9-5'],
  send: ['M21 4 3 11l6 2 2 6 3-4 5 3z'],
  message: ['M4 5h16v11H9l-5 4z'],
  shield: ['M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z'],
  settings: ['M4 7h10', 'M18 7h2', 'M4 17h2', 'M10 17h10', 'M14 4v6', 'M6 14v6'],
  user: ['M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8z', 'M4 21c0-4 3.5-6 8-6s8 2 8 6'],
  check: ['M5 12.5l4.5 4.5L19 7.5'],
  cross: ['M6.5 6.5l11 11', 'M17.5 6.5l-11 11'],
  drop: ['M12 3c4 5 7 8.4 7 12a7 7 0 0 1-14 0c0-3.6 3-7 7-12z'],
  clock: ['M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z', 'M12 7v5l3 2'],
  question: ['M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z', 'M9.5 9.5a2.5 2.5 0 1 1 3.5 2.3c-.7.4-1 .9-1 1.7', 'M12 17h.01'],
  plus: ['M12 5v14', 'M5 12h14'],
  trash: ['M5 7h14', 'M10 11v6', 'M14 11v6', 'M6 7l1 13h10l1-13', 'M9 7V4h6v3'],
  edit: ['M4 20h4L19 9l-4-4L4 16z'],
  link: ['M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1', 'M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1'],
  sun: ['M12 16a4 4 0 1 0 0-8 4 4 0 0 0 0 8z', 'M12 2v2', 'M12 20v2', 'M4.9 4.9l1.4 1.4', 'M17.7 17.7l1.4 1.4', 'M2 12h2', 'M20 12h2', 'M4.9 19.1l1.4-1.4', 'M17.7 6.3l1.4-1.4'],
  moon: ['M20 14.5A8 8 0 0 1 9.5 4 8 8 0 1 0 20 14.5z'],
  palette: ['M12 3a9 9 0 0 0 0 18c1.5 0 2-1 1.5-2s0-2 1.5-2h2a4 4 0 0 0 4-4c0-5-4.5-10-9-10z', 'M7.5 11h.01', 'M10 7.5h.01', 'M14.5 7.5h.01'],
  lock: ['M5 11h14v10H5z', 'M8 11V8a4 4 0 0 1 8 0v3'],
  alert: ['M12 3 2 20h20z', 'M12 10v4', 'M12 17h.01'],
  pin: ['M12 21s7-6.2 7-11a7 7 0 0 0-14 0c0 4.8 7 11 7 11z', 'M12 12a2.5 2.5 0 1 0 0-5 2.5 2.5 0 0 0 0 5z'],
  polygon: ['M5 8l7-4 7 5-2 9-9 2z'],
  filter: ['M4 5h16l-6 8v6l-4-2v-4z'],
  external: ['M14 4h6v6', 'M20 4l-9 9', 'M18 14v5H5V6h5'],
  book: ['M5 4h12a2 2 0 0 1 2 2v14H7a2 2 0 0 1-2-2z', 'M5 18a2 2 0 0 1 2-2h12'],
  inbox: ['M4 13l2-8h12l2 8v6H4z', 'M4 13h5l1 2h4l1-2h5'],
  logout: ['M10 5H5v14h5', 'M15 8l4 4-4 4', 'M9 12h10'],
  menu: ['M4 7h16', 'M4 12h16', 'M4 17h16'],
  chevron: ['M6 9l6 6 6-6'],
  search: ['M11 18a7 7 0 1 0 0-14 7 7 0 0 0 0 14z', 'M16.5 16.5 21 21'],
  info: ['M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z', 'M12 11v5', 'M12 8h.01'],
} as const;

export type IconName = keyof typeof ICONS;

/** Пути иконки — для отрисовки вне Angular-шаблона (HTML-маркеры карты). */
export function iconPaths(name: IconName): readonly string[] {
  return ICONS[name];
}

/** Имя иконки из API произвольное: неизвестное значение заменяем на вопросительный знак. */
export function normalizeIcon(raw: string | null | undefined, fallback: IconName = 'question'): IconName {
  if (!raw) return fallback;
  const key = raw.trim().toLowerCase();
  return Object.hasOwn(ICONS, key) ? (key as IconName) : fallback;
}

@Component({
  selector: 'app-icon',
  template: `<svg class="i" viewBox="0 0 24 24" aria-hidden="true">@for (d of paths(); track $index) {<path [attr.d]="d" />}</svg>`,
  styles: `:host { display: inline-flex; }`,
})
export class Icon {
  readonly name = input.required<IconName>();
  protected readonly paths = computed(() => ICONS[this.name()]);
}
