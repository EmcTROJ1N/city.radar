import { Component, computed, inject, signal } from '@angular/core';
import { FormControl, FormsModule } from '@angular/forms';
import { CategoriesApi } from '@entities/category';
import { StatusDefinitionsApi } from '@entities/status-definition';
import { ApiError, errorText, toApiError } from '@shared/api/api-error';
import type { Category, StatusColor, StatusDefinition, StatusDefinitionInput } from '@shared/api/types';
import { STATUS_COLOR_LABEL } from '@shared/lib/labels';
import { ConfirmService } from '@shared/ui/confirm';
import { FieldError } from '@shared/ui/field-error';
import { FormError } from '@shared/ui/form-error';
import { Icon, IconName, normalizeIcon } from '@shared/ui/icon';
import { StatusBadge } from '@shared/ui/status-badge';
import { ToastService } from '@shared/ui/toast';

const COLORS = (Object.keys(STATUS_COLOR_LABEL) as StatusColor[]).map((value) => ({
  value,
  label: STATUS_COLOR_LABEL[value],
}));

const STATUS_ICONS: IconName[] = ['drop', 'cross', 'check', 'clock', 'alert', 'question'];

interface Draft {
  id: string | null;
  categoryId: string | null;
  code: string;
  name: string;
  color: StatusColor;
  icon: IconName;
  severity: number;
  isPositive: boolean;
}

/** Справочник статусов: заготовки для статусов источников. */
@Component({
  selector: 'app-catalog-statuses',
  imports: [FormsModule, Icon, FieldError, FormError, StatusBadge],
  template: `
    <div class="section-head">
      <div><h2>Справочник статусов</h2><small>Цвет, иконка и вес статуса. По этим записям настраиваются статусы источников.</small></div>
      <button class="btn btn--primary" type="button" (click)="create()"><app-icon name="plus" />Добавить</button>
    </div>

    <div class="split">
      <div class="card card--flush table-wrap">
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
          <p class="empty">Записей пока нет. Создайте первую.</p>
        } @else {
          <table class="table">
            <thead><tr><th>Статус</th><th>Код</th><th>Категория</th><th class="num">Вес</th><th>Позитивный</th><th></th></tr></thead>
            <tbody>
              @for (s of rows(); track s.id) {
                <tr class="is-clickable" [class.is-active]="draft()?.id === s.id" (click)="edit(s)">
                  <td><app-status-badge [label]="s.name" [tone]="s.color" [icon]="iconOf(s.icon)" /></td>
                  <td><code>{{ s.code }}</code></td>
                  <td>{{ categoryName(s.categoryId) }}</td>
                  <td class="num">{{ s.severity }}</td>
                  <td>@if (s.isPositive) { <span class="chip chip--brand">Да</span> } @else { <span class="chip">Нет</span> }</td>
                  <td class="actions">
                    <button class="btn btn--ghost btn--sm" type="button" (click)="edit(s); $event.stopPropagation()"><app-icon name="edit" />Изменить</button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        }
      </div>

      @if (draft(); as d) {
        <form class="card stack" (submit)="save($event)" aria-label="Статус справочника">
          <h2>{{ d.id ? 'Статус справочника' : 'Новая запись справочника' }}</h2>

          <div class="row preview">
            <app-status-badge [label]="d.name.trim() || 'Название статуса'" [tone]="d.color" [icon]="d.icon" />
          </div>

          <div class="field">
            <label for="st-name">Название</label>
            <input id="st-name" name="name" autocomplete="off" [ngModel]="d.name" (ngModelChange)="patch({ name: $event })" />
            @if (nameError()) { <p class="err">{{ nameError() }}</p> }
            <app-field-error [error]="formError()" field="name" />
          </div>

          <div class="field">
            <label for="st-code">Код</label>
            <input id="st-code" name="code" autocomplete="off" spellcheck="false" [ngModel]="d.code" (ngModelChange)="patch({ code: $event })" />
            <app-field-error [error]="formError()" field="code" />
          </div>

          <div class="field">
            <label for="st-category">Категория</label>
            <select id="st-category" name="categoryId" [ngModel]="d.categoryId ?? ''" (ngModelChange)="patch({ categoryId: $event || null })">
              <option value="">Без категории</option>
              @for (c of categories(); track c.id) { <option [value]="c.id">{{ c.name }}</option> }
            </select>
            <app-field-error [error]="formError()" field="categoryId" />
          </div>

          <div class="field">
            <label for="st-color">Цвет</label>
            <select id="st-color" name="color" [ngModel]="d.color" (ngModelChange)="patch({ color: $event })">
              @for (c of colors; track c.value) { <option [value]="c.value">{{ c.label }}</option> }
            </select>
            <app-field-error [error]="formError()" field="color" />
          </div>

          <div class="field">
            <label for="st-icon">Иконка</label>
            <select id="st-icon" name="icon" [ngModel]="d.icon" (ngModelChange)="patch({ icon: $event })">
              @for (name of statusIcons; track name) { <option [value]="name">{{ name }}</option> }
            </select>
            <app-field-error [error]="formError()" field="icon" />
          </div>

          <div class="field">
            <label for="st-severity">Вес (severity)</label>
            <input id="st-severity" name="severity" type="number" min="0" [ngModel]="d.severity"
              (ngModelChange)="patch({ severity: severityOf($event) })" />
            <span class="hint">Чем больше вес, тем важнее статус при выборе итогового.</span>
            <app-field-error [error]="formError()" field="severity" />
          </div>

          <label class="row"><input type="checkbox" name="isPositive" [ngModel]="d.isPositive"
            (ngModelChange)="patch({ isPositive: $event })" />Позитивный статус</label>

          <p class="callout callout--info"><app-icon name="info" /><span>Статусы источника можно создавать из записей справочника.</span></p>

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
        <div class="card empty">Выберите статус в таблице или создайте новый.</div>
      }
    </div>`,
  styles: `
    .pad { padding: var(--pad); }
    .err { font-size: 12px; color: var(--st-none); }
    .preview { padding: 4px 0 0; }
  `,
})
export class CatalogStatuses {
  private readonly api = inject(StatusDefinitionsApi);
  private readonly categoriesApi = inject(CategoriesApi);
  private readonly toasts = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  protected readonly colors = COLORS;
  protected readonly statusIcons = STATUS_ICONS;
  protected readonly fields = ['name', 'code', 'categoryId', 'color', 'icon', 'severity'];
  protected readonly text = errorText;

  protected readonly items = signal<StatusDefinition[]>([]);
  protected readonly categories = signal<Category[]>([]);
  protected readonly loading = signal(false);
  protected readonly loadError = signal<ApiError | null>(null);
  protected readonly draft = signal<Draft | null>(null);
  protected readonly formError = signal<ApiError | null>(null);
  protected readonly saving = signal(false);

  private readonly categoryNames = computed(() => new Map(this.categories().map((c) => [c.id, c.name])));
  protected readonly rows = computed(() =>
    [...this.items()].sort((a, b) => a.severity - b.severity || a.name.localeCompare(b.name, 'ru')),
  );

  protected readonly nameError = computed(() => {
    const d = this.draft();
    if (!d) return '';
    if (!d.name.trim()) return 'Введите название';
    return '';
  });

  protected readonly canSave = computed(() => {
    const d = this.draft();
    return !!d && !this.saving() && !!d.code.trim() && !this.nameError();
  });

  constructor() {
    void this.load();
  }

  protected async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const [statuses, categories] = await Promise.all([this.api.list(), this.categoriesApi.list()]);
      this.items.set(statuses);
      this.categories.set(categories);
    } catch (err) {
      this.loadError.set(toApiError(err));
    } finally {
      this.loading.set(false);
    }
  }

  protected create(): void {
    this.formError.set(null);
    this.draft.set({
      id: null,
      categoryId: null,
      code: '',
      name: '',
      color: 'ok',
      icon: 'drop',
      severity: 0,
      isPositive: true,
    });
  }

  protected edit(status: StatusDefinition): void {
    this.formError.set(null);
    this.draft.set({
      id: status.id,
      categoryId: status.categoryId ?? null,
      code: status.code,
      name: status.name,
      color: status.color,
      icon: normalizeIcon(status.icon),
      severity: status.severity,
      isPositive: status.isPositive,
    });
  }

  protected patch(part: Partial<Draft>): void {
    this.formError.set(null);
    this.draft.update((d) => (d ? { ...d, ...part } : d));
  }

  protected severityOf(value: unknown): number {
    const n = Number(value);
    return Number.isFinite(n) && n > 0 ? Math.floor(n) : 0;
  }

  protected iconOf(icon: string | null | undefined): IconName {
    return normalizeIcon(icon);
  }

  protected categoryName(id: string | null | undefined): string {
    return id ? (this.categoryNames().get(id) ?? '—') : 'Без категории';
  }

  protected async save(event: Event): Promise<void> {
    event.preventDefault();
    const d = this.draft();
    if (!d || !this.canSave()) return;

    const body: StatusDefinitionInput = {
      categoryId: d.categoryId,
      code: d.code.trim(),
      name: d.name.trim(),
      color: d.color,
      icon: d.icon,
      severity: d.severity,
      isPositive: d.isPositive,
    };

    this.saving.set(true);
    this.formError.set(null);
    try {
      if (d.id) {
        const updated = await this.api.update(d.id, body);
        this.items.update((list) => list.map((item) => (item.id === updated.id ? updated : item)));
        this.toasts.success('Статус справочника сохранён');
      } else {
        const created = await this.api.create(body);
        this.items.update((list) => [...list, created]);
        this.toasts.success('Статус справочника создан');
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
      title: `Удалить статус «${name}»?`,
      text: 'Если статус используется в статусах источников, удаление не пройдёт.',
      confirmLabel: 'Удалить',
      danger: true,
    });
    if (!answer.confirmed) return;

    try {
      await this.api.remove(id);
      this.items.update((list) => list.filter((item) => item.id !== id));
      this.draft.update((d) => (d?.id === id ? null : d));
      this.toasts.success('Статус справочника удалён');
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
