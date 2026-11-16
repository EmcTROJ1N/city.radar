import { UI_CONFIG } from './ui.config';

/** Выставляет data-атрибуты на <html>; вызывается один раз до запуска приложения. */
export function applyUiConfig(): void {
  const root = document.documentElement;
  root.dataset['accent'] = UI_CONFIG.accent;
  root.dataset['radius'] = UI_CONFIG.radius;
  root.dataset['density'] = UI_CONFIG.density;
  root.dataset['layout'] = UI_CONFIG.layout;

  if (UI_CONFIG.theme !== 'system') {
    root.dataset['theme'] = UI_CONFIG.theme;
    return;
  }
  const media = matchMedia('(prefers-color-scheme: dark)');
  const sync = () => { root.dataset['theme'] = media.matches ? 'dark' : 'light'; };
  sync();
  media.addEventListener('change', sync);
}
