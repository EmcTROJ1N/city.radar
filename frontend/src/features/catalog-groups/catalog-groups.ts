import { Component, computed, inject, signal } from '@angular/core';
import { FormControl, FormsModule } from '@angular/forms';
import { CategoriesApi } from '@entities/category';
import { GroupsApi } from '@entities/object-group';
import { ApiError, errorText, toApiError } from '@shared/api/api-error';
import type { Category, ObjectGroup, ObjectGroupInput } from '@shared/api/types';
import { ConfirmService } from '@shared/ui/confirm';
import { FieldError } from '@shared/ui/field-error';
import { FormError } from '@shared/ui/form-error';
import { Icon } from '@shared/ui/icon';
import { ToastService } from '@shared/ui/toast';

const DEFAULT_COLOR = '#0e4a5a';

interface Draft {
  id: string | null;
  categoryId: string | null;
  name: string;
  color: string | null;
  isVisibleByDefault: boolean;
}

/** Группы объектов: наборы для быстрого включения на карте. */
@Component({
  selector: 'app-catalog-groups',
  imports: [FormsModule, Icon, FieldError, FormError],
  template: `
    <div class="section-head">
      <div><h2>Группы</h2><small>Наборы фиксаций, которые можно включать и выключать на карте одним движением.</small></div>
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
          <p class="empty">Групп пока нет. Создайте первую.</p>
        } @else {
          <table class="table table--cards">
            <thead><tr><th>Название</th><th>Категория</th><th>Цвет</th><th>Видна по умолчанию</th><th></th></tr></thead>
            <tbody>
              @for (g of rows(); track g.id) {
                <tr class="is-clickable" [class.is-active]="draft()?.id === g.id" (click)="edit(g)">
                  <td class="cell-main"><strong>{{ g.name }}</strong></td>
                  <td data-label="Категория">{{ categoryName(g.categoryId) }}</td>
                  <td data-label="Цвет">
                    @if (g.color) {
                      <span class="swatch"><span class="dot" [style.background]="g.color"></span><code>{{ g.color }}</code></span>
                    } @else {
                      <small>не задан</small>
                    }
                  </td>
                  <td data-label="Видна">@if (g.isVisibleByDefault) { <span class="chip chip--brand">Да</span> } @else { <span class="chip">Нет</span> }</td>
                  <td class="actions">
                    <button class="btn btn--ghost btn--sm" type="button" (click)="edit(g); $event.stopPropagation()"><app-icon name="edit" />Изменить</button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        }
      </div>

      @if (draft(); as d) {
        <form class="card stack" (submit)="save($event)" aria-label="Группа объектов">
          <h2>{{ d.id ? 'Группа' : 'Новая группа' }}</h2>

          <div class="field">
            <label for="gr-name">Название</label>
            <input id="gr-name" name="name" autocomplete="off" [ngModel]="d.name" (ngModelChange)="patch({ name: $event })" />
            @if (nameError()) { <p class="err">{{ nameError() }}</p> }
            <app-field-error [error]="formError()" field="name" />
          </div>

          <div class="field">
            <label for="gr-category">Категория</label>
            <select id="gr-category" name="categoryId" [ngModel]="d.categoryId ?? ''" (ngModelChange)="patch({ categoryId: $event || null })">
              <option value="">Без категории</option>
              @for (c of categories(); track c.id) { <option [value]="c.id">{{ c.name }}</option> }
            </select>
            <app-field-error [error]="formError()" field="categoryId" />
          </div>

          <div class="field">
            <label for="gr-color">Цвет</label>
            <div class="row">
              <input id="gr-color" name="color" type="color" [ngModel]="d.color ?? defaultColor"
                (ngModelChange)="patch({ color: $event })" [disabled]="!d.color" aria-label="Цвет группы" />
              @if (d.color) {
                <button class="btn btn--ghost btn--sm" type="button" (click)="patch({ color: null })">Очистить</button>
              } @else {
                <button class="btn btn--ghost btn--sm" type="button" (click)="patch({ color: defaultColor })">Выбрать цвет</button>
              }
            </div>
            <span class="hint">Без цвета карта подберёт оттенок автоматически.</span>
            <app-field-error [error]="formError()" field="color" />
          </div>

          <label class="row"><input type="checkbox" name="isVisibleByDefault" [ngModel]="d.isVisibleByDefault"
            (ngModelChange)="patch({ isVisibleByDefault: $event })" />Показывать группу на карте по умолчанию</label>

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
        <div class="card empty">Выберите группу в таблице или создайте новую.</div>
      }
    </div>`,
  styles: `
    .pad { padding: var(--pad); }
    .err { font-size: 12px; color: var(--st-none); }
    small { color: var(--ink-3); }
    .swatch { display: inline-flex; align-items: center; gap: 6px; }
    .dot { width: 14px; height: 14px; border-radius: 50%; border: 1px solid var(--line); }
    .field input[type='color'] { width: 56px; padding: 2px 4px; }
  `,
})
export class CatalogGroups {
  private readonly api = inject(GroupsApi);
  private readonly categoriesApi = inject(CategoriesApi);
  private readonly toasts = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  protected readonly fields = ['name', 'categoryId', 'color'];
  protected readonly text = errorText;
  protected readonly defaultColor = DEFAULT_COLOR;

  protected readonly items = signal<ObjectGroup[]>([]);
  protected readonly categories = signal<Category[]>([]);
  protected readonly loading = signal(false);
  protected readonly loadError = signal<ApiError | null>(null);
  protected readonly draft = signal<Draft | null>(null);
  protected readonly formError = signal<ApiError | null>(null);
  protected readonly saving = signal(false);

  private readonly categoryNames = computed(() => new Map(this.categories().map((c) => [c.id, c.name])));
  protected readonly rows = computed(() =>
    [...this.items()].sort((a, b) => a.name.localeCompare(b.name, 'ru')),
  );

  protected readonly nameError = computed(() => {
    const d = this.draft();
    if (!d) return '';
    if (!d.name.trim()) return 'Введите название';
    return '';
  });

  protected readonly canSave = computed(() => !!this.draft() && !this.saving() && !this.nameError());

  constructor() {
    void this.load();
  }

  protected async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const [groups, categories] = await Promise.all([this.api.list(), this.categoriesApi.list()]);
      this.items.set(groups);
      this.categories.set(categories);
    } catch (err) {
      this.loadError.set(toApiError(err));
    } finally {
      this.loading.set(false);
    }
  }

  protected create(): void {
    this.formError.set(null);
    this.draft.set({ id: null, categoryId: null, name: '', color: null, isVisibleByDefault: true });
  }

  protected edit(group: ObjectGroup): void {
    this.formError.set(null);
    this.draft.set({
      id: group.id,
      categoryId: group.categoryId ?? null,
      name: group.name,
      color: group.color ?? null,
      isVisibleByDefault: group.isVisibleByDefault,
    });
  }

  protected patch(part: Partial<Draft>): void {
    this.formError.set(null);
    this.draft.update((d) => (d ? { ...d, ...part } : d));
  }

  protected categoryName(id: string | null | undefined): string {
    return id ? (this.categoryNames().get(id) ?? '—') : 'Без категории';
  }

  protected async save(event: Event): Promise<void> {
    event.preventDefault();
    const d = this.draft();
    if (!d || !this.canSave()) return;

    const body: ObjectGroupInput = {
      categoryId: d.categoryId,
      name: d.name.trim(),
      color: d.color,
      isVisibleByDefault: d.isVisibleByDefault,
    };

    this.saving.set(true);
    this.formError.set(null);
    try {
      if (d.id) {
        const updated = await this.api.update(d.id, body);
        this.items.update((list) => list.map((item) => (item.id === updated.id ? updated : item)));
        this.toasts.success('Группа сохранена');
      } else {
        const created = await this.api.create(body);
        this.items.update((list) => [...list, created]);
        this.toasts.success('Группа создана');
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
      title: `Удалить группу «${name}»?`,
      text: 'Группа исчезнет из фильтров карты. Фиксации останутся, но без группы.',
      confirmLabel: 'Удалить',
      danger: true,
    });
    if (!answer.confirmed) return;

    try {
      await this.api.remove(id);
      this.items.update((list) => list.filter((item) => item.id !== id));
      this.draft.update((d) => (d?.id === id ? null : d));
      this.toasts.success('Группа удалена');
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
