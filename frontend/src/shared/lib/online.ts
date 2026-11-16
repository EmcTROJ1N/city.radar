import { DestroyRef, Signal, inject, signal } from '@angular/core';

/** Наличие сети в браузере: офлайн-показ кэша карты. */
export function onlineSignal(): Signal<boolean> {
  const online = signal(typeof navigator === 'undefined' ? true : navigator.onLine);
  const update = () => online.set(navigator.onLine);
  window.addEventListener('online', update);
  window.addEventListener('offline', update);
  inject(DestroyRef).onDestroy(() => {
    window.removeEventListener('online', update);
    window.removeEventListener('offline', update);
  });
  return online.asReadonly();
}
