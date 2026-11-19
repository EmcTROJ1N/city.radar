import { Injectable, signal } from '@angular/core';
import { storage } from '../lib/storage';
import { UI_CONFIG } from './ui.config';

export type ResolvedTheme = 'light' | 'dark';

// Выбор пользователя запоминается; пока он не сделан, тема берётся из UI_CONFIG (system — как на устройстве).
const STORAGE_KEY = 'ui.theme';

const current = signal<ResolvedTheme>('light');

function stored(): ResolvedTheme | null {
  const value = storage.get(STORAGE_KEY);
  return value === 'light' || value === 'dark' ? value : null;
}

function apply(theme: ResolvedTheme): void {
  document.documentElement.dataset['theme'] = theme;
  current.set(theme);
}

/** Ставит data-theme на <html>; вызывается один раз до запуска приложения. */
export function initTheme(): void {
  const media = matchMedia('(prefers-color-scheme: dark)');
  const fallback = (): ResolvedTheme =>
    UI_CONFIG.theme !== 'system' ? UI_CONFIG.theme : media.matches ? 'dark' : 'light';
  apply(stored() ?? fallback());
  media.addEventListener('change', () => { if (!stored()) apply(fallback()); });
}

@Injectable({ providedIn: 'root' })
export class ThemeService {
  readonly theme = current.asReadonly();

  toggle(): void {
    const next: ResolvedTheme = current() === 'dark' ? 'light' : 'dark';
    storage.set(STORAGE_KEY, next);
    apply(next);
  }
}
