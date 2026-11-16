import { DestroyRef, Signal, inject, signal } from '@angular/core';

/** Видимость вкладки: опрос карты ставится на паузу, когда вкладка скрыта. */
export function pageVisible(): Signal<boolean> {
  const visible = signal(typeof document === 'undefined' ? true : document.visibilityState === 'visible');
  const onChange = () => visible.set(document.visibilityState === 'visible');
  document.addEventListener('visibilitychange', onChange);
  inject(DestroyRef).onDestroy(() => document.removeEventListener('visibilitychange', onChange));
  return visible.asReadonly();
}
