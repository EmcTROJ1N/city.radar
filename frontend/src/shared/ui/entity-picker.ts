import { Component, DestroyRef, ElementRef, HostListener, computed, inject, input, model, signal, viewChild } from '@angular/core';
import { Icon } from './icon';

export interface PickerOption {
  value: string;
  label: string;
  hint?: string;
}

export type PickerLoader = (
  q: string,
  cursor: string | null,
) => Promise<{ items: PickerOption[]; nextCursor: string | null }>;

const DEBOUNCE_MS = 300;
let seq = 0;

/**
 * Выбор сущности из большого списка: поиск и подгрузка страниц с сервера.
 * Значение всегда массив: для одиночного выбора — 0 или 1 элемент.
 */
@Component({
  selector: 'app-entity-picker',
  imports: [Icon],
  template: `
    <div class="ep">
      <div class="ep__control">
        <button
          type="button"
          class="ep__btn"
          role="combobox"
          aria-haspopup="listbox"
          [attr.aria-expanded]="open()"
          [attr.aria-controls]="listId"
          [disabled]="disabled()"
          (click)="toggle()"
          (keydown)="onTriggerKey($event)"
        >
          <span class="grow" [class.muted]="!value().length">{{ summary() }}</span>
          <app-icon name="chevron" />
        </button>
        @if (!multiple() && value().length) {
          <button type="button" class="ep__clear" aria-label="Очистить" (click)="clear($event)">
            <app-icon name="cross" />
          </button>
        }
      </div>

      @if (open()) {
        <div class="ep__panel">
          <input
            #search
            class="ep__search"
            type="search"
            role="combobox"
            aria-autocomplete="list"
            aria-expanded="true"
            [attr.aria-controls]="listId"
            placeholder="Поиск"
            aria-label="Поиск"
            [value]="query()"
            (input)="onQuery($any($event.target).value)"
            (keydown)="onKey($event)"
          />
          <ul
            class="ep__list"
            [id]="listId"
            role="listbox"
            [attr.aria-multiselectable]="multiple()"
            (scroll)="onScroll($event)"
          >
            @for (option of items(); track option.value; let i = $index) {
              <li
                role="option"
                [attr.aria-selected]="isSelected(option.value)"
                [class.is-active]="i === active()"
                (click)="choose(option)"
                (mouseenter)="active.set(i)"
              >
                <span class="ep__check">@if (isSelected(option.value)) { <app-icon name="check" /> }</span>
                <span class="grow">{{ option.label }}</span>
                @if (option.hint) { <small>{{ option.hint }}</small> }
              </li>
            } @empty {
              <li class="ep__empty">{{ emptyText() }}</li>
            }
          </ul>
          @if (nextCursor()) {
            <div class="ep__more">
              <button type="button" class="btn btn--sm btn--ghost" [disabled]="loading()" (click)="loadMore()">
                {{ loading() ? 'Загрузка…' : 'Ещё' }}
              </button>
            </div>
          }
        </div>
      }

      @if (multiple() && chips().length) {
        <div class="row ep__chips">
          @for (option of chips(); track option.value) {
            <span class="chip chip--brand">
              {{ option.label }}
              <button type="button" [attr.aria-label]="'Убрать: ' + option.label" (click)="remove(option.value)">
                <app-icon name="cross" />
              </button>
            </span>
          }
        </div>
      }
    </div>`,
  styles: `
    :host { display: block; }
    .ep { position: relative; display: grid; gap: 8px; }
    .ep__control { position: relative; }
    .ep__btn { display: flex; align-items: center; gap: 8px; width: 100%; height: var(--control-h); padding: 0 12px;
      text-align: left; cursor: pointer; background: var(--surface); border: 1px solid var(--line); border-radius: var(--r-md); }
    .ep__btn[aria-expanded='true'] { border-color: var(--brand); box-shadow: var(--focus-ring); }
    .ep__btn:disabled { opacity: 0.5; cursor: not-allowed; }
    .ep__clear { position: absolute; top: 50%; right: 34px; transform: translateY(-50%); display: inline-flex;
      padding: 4px; border: 0; background: transparent; color: var(--ink-3); cursor: pointer; }
    .ep__panel { position: absolute; top: calc(var(--control-h) + 4px); left: 0; right: 0; z-index: 15;
      display: grid; gap: 8px; padding: 8px; background: var(--surface); border: 1px solid var(--line);
      border-radius: var(--r-md); box-shadow: var(--shadow-float); }
    .ep__search { height: 34px; padding: 0 10px; border: 1px solid var(--line); border-radius: var(--r-sm); background: var(--surface-2); }
    .ep__list { list-style: none; margin: 0; padding: 0; max-height: 260px; overflow: auto; }
    .ep__list li[role='option'] { display: flex; align-items: center; gap: 8px; padding: 7px 8px; border-radius: var(--r-sm); cursor: pointer; }
    .ep__list li[role='option']:hover, .ep__list li.is-active { background: var(--surface-2); }
    .ep__check { display: inline-flex; width: 16px; color: var(--brand); }
    .ep__empty { padding: 12px; color: var(--ink-3); text-align: center; }
    .ep__more { display: flex; justify-content: center; }
    .ep__chips { gap: 6px; }
  `,
})
export class EntityPicker {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly loader = input.required<PickerLoader>();
  readonly multiple = input(false);
  readonly placeholder = input('Выберите');
  /** Подписи уже выбранных значений, которых нет в загруженной странице. */
  readonly labels = input<Record<string, string>>({});
  readonly disabled = input(false);
  readonly value = model<string[]>([]);

  protected readonly listId = `ep-list-${++seq}`;
  protected readonly open = signal(false);
  protected readonly query = signal('');
  protected readonly items = signal<PickerOption[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly failed = signal(false);
  protected readonly active = signal(0);

  private readonly search = viewChild<ElementRef<HTMLInputElement>>('search');
  private timer: ReturnType<typeof setTimeout> | null = null;
  private token = 0;

  protected readonly chips = computed(() => {
    const labels = this.labels();
    const loaded = this.items();
    return this.value().map(
      (v) => loaded.find((option) => option.value === v) ?? { value: v, label: labels[v] ?? v },
    );
  });

  protected readonly summary = computed(() => {
    const count = this.value().length;
    if (!count) return this.placeholder();
    if (this.multiple()) return `Выбрано: ${count}`;
    return this.chips()[0]?.label ?? this.placeholder();
  });

  protected readonly emptyText = computed(() => {
    if (this.loading()) return 'Загрузка…';
    return this.failed() ? 'Не удалось загрузить' : 'Ничего не найдено';
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.clearTimer());
  }

  protected toggle(): void {
    if (this.disabled()) return;
    if (this.open()) this.closePanel();
    else this.openPanel();
  }

  protected onTriggerKey(event: KeyboardEvent): void {
    if (event.key === 'ArrowDown' && !this.open()) {
      event.preventDefault();
      this.openPanel();
    }
  }

  protected onQuery(raw: string): void {
    this.query.set(raw);
    this.active.set(0);
    this.clearTimer();
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.load(true);
    }, DEBOUNCE_MS);
  }

  protected loadMore(): void {
    if (this.loading()) return;
    void this.load(false);
  }

  protected onScroll(event: Event): void {
    const el = event.target as HTMLElement;
    if (el.scrollTop + el.clientHeight >= el.scrollHeight - 8) this.loadMore();
  }

  protected isSelected(value: string): boolean {
    return this.value().includes(value);
  }

  protected choose(option: PickerOption): void {
    if (this.multiple()) {
      this.value.update((list) =>
        list.includes(option.value) ? list.filter((v) => v !== option.value) : [...list, option.value],
      );
      return;
    }
    this.value.set([option.value]);
    this.closePanel();
  }

  protected remove(value: string): void {
    this.value.update((list) => list.filter((v) => v !== value));
  }

  protected clear(event: Event): void {
    event.stopPropagation();
    this.value.set([]);
  }

  protected onKey(event: KeyboardEvent): void {
    const list = this.items();
    if (event.key === 'Escape') {
      event.preventDefault();
      this.closePanel();
      return;
    }
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      if (list.length) this.active.update((i) => Math.min(i + 1, list.length - 1));
      return;
    }
    if (event.key === 'ArrowUp') {
      event.preventDefault();
      this.active.update((i) => Math.max(i - 1, 0));
      return;
    }
    if (event.key === 'Enter') {
      const option = list[this.active()];
      if (option) {
        event.preventDefault();
        this.choose(option);
      }
    }
  }

  @HostListener('document:click', ['$event'])
  protected onDocClick(event: Event): void {
    if (this.open() && !this.host.nativeElement.contains(event.target as Node)) this.closePanel();
  }

  private openPanel(): void {
    this.open.set(true);
    this.query.set('');
    this.active.set(0);
    this.failed.set(false);
    void this.load(true);
    setTimeout(() => this.search()?.nativeElement.focus());
  }

  private closePanel(): void {
    this.open.set(false);
    this.clearTimer();
  }

  private async load(reset: boolean): Promise<void> {
    if (!reset && (!this.nextCursor() || this.loading())) return;
    const cursor = reset ? null : this.nextCursor();
    if (reset) {
      this.items.set([]);
      this.nextCursor.set(null);
    }
    const token = ++this.token;
    this.loading.set(true);
    try {
      const page = await this.loader()(this.query().trim(), cursor);
      if (token !== this.token) return;
      this.items.update((list) => (reset ? page.items : this.merge(list, page.items)));
      this.nextCursor.set(page.nextCursor);
      this.failed.set(false);
    } catch {
      if (token !== this.token) return;
      this.failed.set(true);
      this.nextCursor.set(null);
    } finally {
      if (token === this.token) this.loading.set(false);
    }
  }

  private merge(list: PickerOption[], page: PickerOption[]): PickerOption[] {
    const seen = new Set(list.map((option) => option.value));
    return [...list, ...page.filter((option) => !seen.has(option.value))];
  }

  private clearTimer(): void {
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
  }
}
