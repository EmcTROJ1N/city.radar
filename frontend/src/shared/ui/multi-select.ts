import { Component, ElementRef, HostListener, computed, inject, input, model, signal } from '@angular/core';
import { Icon } from './icon';

export interface SelectOption { value: string; label: string; hint?: string; }

/** Выпадающий список с поиском и множественным выбором: элементов может быть сотни. */
@Component({
  selector: 'app-multi-select',
  imports: [Icon],
  template: `
    <div class="ms">
      <button type="button" class="ms__btn" [attr.aria-expanded]="open()" aria-haspopup="listbox" (click)="open.set(!open())">
        <span class="grow" [class.muted]="!selected().length">{{ summary() }}</span>
        <app-icon name="chevron" />
      </button>

      @if (open()) {
        <div class="ms__panel" role="listbox" aria-multiselectable="true" (keydown.escape)="open.set(false)">
          <input class="ms__search" type="search" placeholder="Поиск" aria-label="Поиск в списке" [value]="query()" (input)="query.set($any($event.target).value)" />
          @if (bulk()) {
            <div class="row">
              <button type="button" class="btn btn--sm" (click)="selectAll()">Все</button>
              <button type="button" class="btn btn--sm btn--ghost" (click)="clearAll()">Снять все</button>
            </div>
          }
          <ul class="ms__list">
            @for (o of filtered(); track o.value) {
              <li>
                <label class="ms__item">
                  <input type="checkbox" [checked]="isOn(o.value)" (change)="toggle(o.value)" />
                  <span class="grow">{{ o.label }}</span>
                  @if (o.hint) { <small>{{ o.hint }}</small> }
                </label>
              </li>
            } @empty {
              <li class="ms__empty">{{ options().length ? 'Ничего не найдено' : emptyText() }}</li>
            }
          </ul>
        </div>
      }

      @if (showChips() && chips().length) {
        <div class="row ms__chips">
          @for (o of chips(); track o.value) {
            <span class="chip chip--brand">{{ o.label }}<button type="button" [attr.aria-label]="'Убрать: ' + o.label" (click)="toggle(o.value)"><app-icon name="cross" /></button></span>
          }
        </div>
      }
    </div>`,
  styles: `
    :host { display: block; }
    .ms { position: relative; display: grid; gap: 8px; }
    .ms__btn { display: flex; align-items: center; gap: 8px; width: 100%; height: var(--control-h); padding: 0 12px; text-align: left; cursor: pointer;
      background: var(--surface); border: 1px solid var(--line); border-radius: var(--r-md); }
    .ms__btn[aria-expanded='true'] { border-color: var(--brand); box-shadow: var(--focus-ring); }
    .ms__panel { position: absolute; top: calc(var(--control-h) + 4px); left: 0; right: 0; z-index: 15; background: var(--surface);
      border: 1px solid var(--line); border-radius: var(--r-md); box-shadow: var(--shadow-float); padding: 8px; display: grid; gap: 8px; }
    .ms__search { height: 34px; padding: 0 10px; border: 1px solid var(--line); border-radius: var(--r-sm); background: var(--surface-2); }
    .ms__list { list-style: none; margin: 0; padding: 0; max-height: 260px; overflow: auto; }
    .ms__item { display: flex; align-items: center; gap: 8px; padding: 7px 8px; border-radius: var(--r-sm); cursor: pointer; }
    .ms__item:hover { background: var(--surface-2); }
    .ms__item input { height: auto; width: 16px; padding: 0; }
    .ms__empty { padding: 12px; color: var(--ink-3); text-align: center; }
    .ms__chips { gap: 6px; }
  `,
})
export class MultiSelect {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly options = input.required<SelectOption[]>();
  readonly placeholder = input('Выберите');
  readonly emptyText = input('Список пуст');
  /** Кнопки «Все» и «Снять все» (действуют на найденные по поиску элементы). */
  readonly bulk = input(false);
  readonly showChips = input(true);
  /** Подпись, когда выбраны все элементы. */
  readonly allLabel = input('');
  readonly selected = model<string[]>([]);

  protected readonly open = signal(false);
  protected readonly query = signal('');

  protected readonly filtered = computed(() => {
    const q = this.query().trim().toLowerCase();
    return q ? this.options().filter((o) => o.label.toLowerCase().includes(q)) : this.options();
  });
  protected readonly chips = computed(() => this.options().filter((o) => this.selected().includes(o.value)));
  protected readonly summary = computed(() => {
    const n = this.selected().length;
    const total = this.options().length;
    if (this.allLabel() && total && n === total) return this.allLabel();
    if (!n) return this.bulk() ? 'Ничего не выбрано' : this.placeholder();
    return this.bulk() ? `Выбрано: ${n} из ${total}` : `Выбрано: ${n}`;
  });

  protected selectAll(): void {
    this.selected.update((s) => [...new Set([...s, ...this.filtered().map((o) => o.value)])]);
  }
  protected clearAll(): void {
    const drop = new Set(this.filtered().map((o) => o.value));
    this.selected.update((s) => s.filter((v) => !drop.has(v)));
  }

  protected isOn(v: string): boolean { return this.selected().includes(v); }
  protected toggle(v: string): void {
    this.selected.update((s) => (s.includes(v) ? s.filter((x) => x !== v) : [...s, v]));
  }

  @HostListener('document:click', ['$event'])
  protected onDocClick(e: Event): void {
    if (this.open() && !this.host.nativeElement.contains(e.target as Node)) this.open.set(false);
  }
}
