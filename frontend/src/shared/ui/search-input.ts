import { Component, DestroyRef, effect, inject, input, model, signal } from '@angular/core';
import { Icon } from './icon';

/** Поле поиска с иконкой и очисткой; значение обновляется с задержкой. */
@Component({
  selector: 'app-search-input',
  imports: [Icon],
  template: `
    <div class="si">
      <app-icon name="search" />
      <input
        type="search"
        [value]="text()"
        [placeholder]="placeholder()"
        [attr.aria-label]="placeholder()"
        (input)="onInput($any($event.target).value)"
      />
      @if (text()) {
        <button type="button" class="btn btn--ghost btn--icon btn--sm si__clear" aria-label="Очистить" (click)="clear()">
          <app-icon name="cross" />
        </button>
      }
    </div>`,
  styles: `
    .si { display: flex; align-items: center; gap: 8px; height: var(--control-h); padding: 0 6px 0 10px;
      background: var(--surface); border: 1px solid var(--line); border-radius: var(--r-md); }
    .si:focus-within { border-color: var(--brand); box-shadow: var(--focus-ring); }
    .si input { flex: 1 1 auto; min-width: 0; height: 100%; padding: 0; border: 0; background: transparent; outline: none; }
    .si input::-webkit-search-cancel-button { display: none; }
    .si__clear { width: 28px; }
  `,
})
export class SearchInput {
  readonly value = model('');
  readonly placeholder = input('Поиск');
  readonly debounce = input(300);

  protected readonly text = signal('');

  private timer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    effect(() => {
      this.text.set(this.value());
    });
    inject(DestroyRef).onDestroy(() => this.clearTimer());
  }

  protected onInput(raw: string): void {
    this.text.set(raw);
    this.clearTimer();
    const wait = Math.max(0, this.debounce());
    if (!wait) {
      this.commit(raw);
      return;
    }
    this.timer = setTimeout(() => {
      this.timer = null;
      this.commit(raw);
    }, wait);
  }

  protected clear(): void {
    this.clearTimer();
    this.text.set('');
    this.commit('');
  }

  private commit(raw: string): void {
    if (this.value() !== raw) this.value.set(raw);
  }

  private clearTimer(): void {
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
  }
}
