import { Component, ElementRef, Injectable, computed, effect, inject, signal, viewChild } from '@angular/core';
import { errorText, toApiError } from '../api/api-error';
import { Icon } from './icon';

export type ToastKind = 'success' | 'error' | 'info';

export interface Toast {
  id: number;
  kind: ToastKind;
  text: string;
}

const LIFETIME_MS = 5000;

@Injectable({ providedIn: 'root' })
export class ToastService {
  private seq = 0;
  private readonly items = signal<Toast[]>([]);
  readonly toasts = this.items.asReadonly();
  readonly count = computed(() => this.items().length);

  success(text: string): void {
    this.push('success', text);
  }

  error(text: string): void {
    this.push('error', text);
  }

  info(text: string): void {
    this.push('info', text);
  }

  fromError(e: unknown): void {
    this.error(errorText(toApiError(e)));
  }

  dismiss(id: number): void {
    this.items.update((list) => list.filter((t) => t.id !== id));
  }

  private push(kind: ToastKind, text: string): void {
    const id = ++this.seq;
    this.items.update((list) => [...list, { id, kind, text }]);
    setTimeout(() => this.dismiss(id), LIFETIME_MS);
  }
}

@Component({
  selector: 'app-toasts',
  imports: [Icon],
  template: `
    @if (toasts().length) {
      <div #box class="toasts" popover="manual" role="status" aria-live="polite">
        @for (t of toasts(); track t.id) {
          <div class="toast" [class]="'toast--' + t.kind">
            <span class="grow">{{ t.text }}</span>
            <button class="btn btn--ghost btn--icon btn--sm" type="button" aria-label="Закрыть" (click)="service.dismiss(t.id)">
              <app-icon name="cross" />
            </button>
          </div>
        }
      </div>
    }`,
  styles: `
    // popover сбрасывает позицию и оформление UA-стилями: возвращаем свои.
    .toasts { position: fixed; inset: auto var(--gap) var(--gap) auto; margin: 0; padding: 0; border: 0;
      background: transparent; color: inherit; overflow: visible; z-index: 60;
      display: grid; gap: 8px; width: min(360px, calc(100vw - var(--gap) * 2)); }
    .toast { display: flex; align-items: center; gap: 8px; padding: 10px 10px 10px 14px; border-radius: var(--r-md);
      background: var(--surface); color: var(--ink); border: 1px solid var(--line); box-shadow: var(--shadow-float); }
    .toast--success { border-left: 4px solid var(--st-ok); }
    .toast--error { border-left: 4px solid var(--st-none); }
    .toast--info { border-left: 4px solid var(--brand); }
    .toast .btn--sm { width: 30px; }
  `,
})
export class Toasts {
  protected readonly service = inject(ToastService);
  protected readonly toasts = this.service.toasts;

  private readonly box = viewChild<ElementRef<HTMLElement>>('box');
  private shownCount = 0;

  constructor() {
    // Модальный <dialog> лежит в top layer поверх любого z-index. Тосты выводим туда же
    // как popover и переоткрываем на каждый новый тост — так он оказывается выше окна.
    effect(() => {
      const count = this.toasts().length;
      const box = this.box()?.nativeElement;
      if (!box) {
        this.shownCount = 0;
        return;
      }
      const grew = count > this.shownCount;
      this.shownCount = count;
      if (!grew || typeof box.showPopover !== 'function') return;
      // data-open: контейнер уже показан нами (jsdom не знает :popover-open).
      if (box.hasAttribute('data-open')) box.hidePopover();
      box.showPopover();
      box.setAttribute('data-open', '');
    });
  }
}
