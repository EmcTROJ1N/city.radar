import { Component, computed, inject, signal } from '@angular/core';
import { FormControl, FormsModule } from '@angular/forms';
import { CategoriesApi } from '@entities/category';
import { ApiError, errorText, toApiError } from '@shared/api/api-error';
import type { Category, CategoryInput } from '@shared/api/types';
import { slugCode } from '@shared/lib/code';
import { ConfirmService } from '@shared/ui/confirm';
import { FieldError } from '@shared/ui/field-error';
import { FormError } from '@shared/ui/form-error';
import { Icon, IconName, normalizeIcon } from '@shared/ui/icon';
import { ToastService } from '@shared/ui/toast';

const CODE_PATTERN = /^[a-z0-9_]+$/;

const ICONS: IconName[] = [
  'drop', 'pin', 'polygon', 'map', 'layers', 'book', 'palette', 'filter', 'inbox', 'message',
  'send', 'link', 'external', 'search', 'check', 'cross', 'clock', 'question', 'alert', 'info',
  'user', 'shield', 'settings', 'lock', 'sun', 'moon', 'plus', 'edit', 'trash', 'chevron', 'menu', 'logout',
];

interface Draft {
  id: string | null;
  code: string;
  name: string;
  icon: string;
  sortOrder: number;
  isActive: boolean;
  codeTouched: boolean;
}

/** Категории ресурса. */
@Component({
  selector: 'app-catalog-categories',
  imports: [FormsModule, Icon, FieldError, FormError],
  template: `
    <div class="section-head">
      <div><h2>Категории</h2><small>Вид ресурса: вода, прочие сети.</small></div>
      <button class="btn btn--primary" type="button" (click)="create()"><app-icon name="plus" />Добавить</button>
    </div>

    <div class="split">
      <div class="card card--flush table-wrap table-wrap--cards">
        @if (loadError(); as e) {
          <div class="pad">
            <div class="callout" role="alert">
              <app-icon name="alert" />
              <span class="grow">{{ text(e) }}</span>
              <button class="btn btn--sm" type="button" (click)="load()">Повторить</button>
            </div>
          </div>
        } @else if (loading() && !rows().length) {
          <p class="empty">Загрузка…</p>
        } @else if (!rows().length) {
          <p class="empty">Категорий пока нет. Создайте первую.</p>
        } @else {
          <table class="table table--cards">
            <thead><tr><th>Код</th><th>Название</th><th>Иконка</th><th class="num">Порядок</th><th>Активна</th><th></th></tr></thead>
            <tbody>
              @for (c of rows(); track c.id) {
                <tr class="is-clickable" [class.is-active]="draft()?.id === c.id" (click)="edit(c)">
                  <td data-label="Код"><code>{{ c.code }}</code></td>
                  <td class="cell-main"><strong>{{ c.name }}</strong></td>
                  <td data-label="Иконка"><span class="row"><app-icon [name]="iconOf(c.icon)" />{{ c.icon ?? '—' }}</span></td>
                  <td class="num" data-label="Порядок">{{ c.sortOrder }}</td>
                  <td data-label="Активна">@if (c.isActive) { <span class="chip chip--brand">Да</span> } @else { <span class="chip">Нет</span> }</td>
                  <td class="actions">
                    <button class="btn btn--ghost btn--sm" type="button" (click)="edit(c); $event.stopPropagation()"><app-icon name="edit" />Изменить</button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        }
      </div>

      @if (draft(); as d) {
        <form class="card stack" (submit)="save($event)" aria-label="Категория">
          <h2>{{ d.id ? 'Категория' : 'Новая категория' }}</h2>

          <div class="field">
            <label for="cat-name">Название</label>
            <input id="cat-name" name="name" autocomplete="off" [ngModel]="d.name" (ngModelChange)="setName($event)" />
            @if (nameError()) { <p class="err">{{ nameError() }}</p> }
            <app-field-error [error]="formError()" field="name" />
          </div>

          <div class="field">
            <label for="cat-code">Код</label>
            <input id="cat-code" name="code" autocomplete="off" spellcheck="false" [ngModel]="d.code" (ngModelChange)="setCode($event)" />
            <span class="hint">Латинские строчные буквы, цифры и подчёркивание. Подставляется из названия.</span>
            @if (codeError()) { <p class="err">{{ codeError() }}</p> }
            <app-field-error [error]="formError()" field="code" />
          </div>

          <div class="field">
            <label for="cat-icon">Иконка</label>
            <select id="cat-icon" name="icon" [ngModel]="d.icon" (ngModelChange)="patch({ icon: $event })">
              <option value="">Без иконки</option>
              @for (name of icons; track name) { <option [value]="name">{{ name }}</option> }
            </select>
            <app-field-error [error]="formError()" field="icon" />
          </div>

          <div class="field">
            <label for="cat-order">Порядок</label>
            <input id="cat-order" name="sortOrder" type="number" [ngModel]="d.sortOrder" (ngModelChange)="patch({ sortOrder: orderOf($event) })" />
            <app-field-error [error]="formError()" field="sortOrder" />
          </div>

          <label class="row"><input type="checkbox" name="isActive" [ngModel]="d.isActive" (ngModelChange)="patch({ isActive: $event })" />Активна</label>

          <app-form-error [error]="formError()" [fields]="fields" />

          <div class="row">
            <button class="btn btn--primary" type="submit" [disabled]="!canSave()">{{ saving() ? 'Сохранение…' : 'Сохранить' }}</button>
            <button class="btn btn--ghost" type="button" (click)="close()">Отмена</button>
            @if (d.id) {
              <button class="btn btn--ghost btn--danger" type="button" (click)="remove(d.id, d.name)"><app-icon name="trash" />Удалить</button>
            }
          </div>
        </form>
      } @else {
        <div class="card empty">Выберите категорию в таблице или создайте новую.</div>
      }
    </div>`,
  styles: `
    .pad { padding: var(--pad); }
    .err { font-size: 12px; color: var(--st-none); }
    small { color: var(--ink-3); }
  `,
})
export class CatalogCategories {
  private readonly api = inject(CategoriesApi);
  private readonly toasts = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  protected readonly icons = ICONS;
  protected readonly fields = ['name', 'code', 'icon', 'sortOrder'];
  protected readonly text = errorText;

  protected readonly items = signal<Category[]>([]);
  protected readonly loading = signal(false);
  protected readonly loadError = signal<ApiError | null>(null);
  protected readonly draft = signal<Draft | null>(null);
  protected readonly formError = signal<ApiError | null>(null);
  protected readonly saving = signal(false);

  protected readonly rows = computed(() =>
    [...this.items()].sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name, 'ru')),
  );

  protected readonly nameError = computed(() => {
    const d = this.draft();
    if (!d) return '';
    if (!d.name.trim()) return 'Введите название';
    return '';
  });

  protected readonly codeError = computed(() => {
    const d = this.draft();
    if (!d) return '';
    if (!d.code.trim()) return 'Введите код';
    return CODE_PATTERN.test(d.code.trim()) ? '' : 'Только латинские строчные буквы, цифры и подчёркивание';
  });

  protected readonly canSave = computed(() => !!this.draft() && !this.saving() && !this.nameError() && !this.codeError());

  constructor() {
    void this.load();
  }

  protected async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      this.items.set(await this.api.list());
    } catch (err) {
      this.loadError.set(toApiError(err));
    } finally {
      this.loading.set(false);
    }
  }

  protected create(): void {
    const order = this.items().reduce((max, item) => Math.max(max, item.sortOrder), 0) + 1;
    this.formError.set(null);
    this.draft.set({ id: null, code: '', name: '', icon: 'drop', sortOrder: order, isActive: true, codeTouched: false });
  }

  protected edit(category: Category): void {
    this.formError.set(null);
    this.draft.set({
      id: category.id,
      code: category.code,
      name: category.name,
      icon: category.icon ?? '',
      sortOrder: category.sortOrder,
      isActive: category.isActive,
      codeTouched: true,
    });
  }

  protected setName(name: string): void {
    const d = this.draft();
    if (!d) return;
    this.formError.set(null);
    this.draft.set({ ...d, name, ...(d.codeTouched ? {} : { code: slugCode(name) }) });
  }

  protected setCode(value: string): void {
    this.patch({ code: value, codeTouched: true });
  }

  protected patch(part: Partial<Draft>): void {
    this.formError.set(null);
    this.draft.update((d) => (d ? { ...d, ...part } : d));
  }

  protected orderOf(value: unknown): number {
    const n = Number(value);
    return Number.isFinite(n) ? n : 0;
  }

  protected iconOf(icon: string | null | undefined): IconName {
    return normalizeIcon(icon);
  }

  protected async save(event: Event): Promise<void> {
    event.preventDefault();
    const d = this.draft();
    if (!d || !this.canSave()) return;

    const body: CategoryInput = {
      code: d.code.trim(),
      name: d.name.trim(),
      icon: d.icon || null,
      sortOrder: d.sortOrder,
      isActive: d.isActive,
    };

    this.saving.set(true);
    this.formError.set(null);
    try {
      if (d.id) {
        const updated = await this.api.update(d.id, body);
        this.items.update((list) => list.map((item) => (item.id === updated.id ? updated : item)));
        this.toasts.success('Категория сохранена');
      } else {
        const created = await this.api.create(body);
        this.items.update((list) => [...list, created]);
        this.toasts.success('Категория создана');
      }
      this.draft.set(null);
    } catch (err) {
      this.fail(err);
    } finally {
      this.saving.set(false);
    }
  }

  protected async remove(id: string, name: string): Promise<void> {
    const answer = await this.confirm.ask({
      title: `Удалить категорию «${name}»?`,
      text: 'Категория исчезнет из фильтров и форм. Если к ней привязаны типы объектов, удаление не пройдёт.',
      confirmLabel: 'Удалить',
      danger: true,
    });
    if (!answer.confirmed) return;

    try {
      await this.api.remove(id);
      this.items.update((list) => list.filter((item) => item.id !== id));
      this.draft.update((d) => (d?.id === id ? null : d));
      this.toasts.success('Категория удалена');
    } catch (err) {
      this.toasts.error(errorText(toApiError(err)));
    }
  }

  protected close(): void {
    this.draft.set(null);
    this.formError.set(null);
  }

  private fail(err: unknown): void {
    const error = toApiError(err);
    if (error.status === 422) this.formError.set(error);
    else this.toasts.error(errorText(error));
  }
}
