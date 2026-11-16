import { Component, computed, inject, signal } from '@angular/core';
import { FormControl, FormsModule } from '@angular/forms';
import { CategoriesApi } from '@entities/category';
import { ObjectTypesApi } from '@entities/object-type';
import { ApiError, errorText, toApiError } from '@shared/api/api-error';
import type { Category, ObjectType, ObjectTypeInput } from '@shared/api/types';
import { ConfirmService } from '@shared/ui/confirm';
import { FieldError } from '@shared/ui/field-error';
import { FormError } from '@shared/ui/form-error';
import { Icon } from '@shared/ui/icon';
import { ToastService } from '@shared/ui/toast';
import { EXAMPLE_SCHEMA, parseAttributesSchema } from './attributes-schema';

type GeometryKind = ObjectTypeInput['geometryKind'];

const GEOMETRY: { value: GeometryKind; label: string }[] = [
  { value: 'point', label: 'Точка' },
  { value: 'polygon', label: 'Геозона' },
  { value: 'any', label: 'Любая' },
];

interface Draft {
  id: string | null;
  categoryId: string;
  code: string;
  name: string;
  geometryKind: GeometryKind;
  schemaText: string;
}

/** Типы объектов и JSON-схема их атрибутов. */
@Component({
  selector: 'app-catalog-object-types',
  imports: [FormsModule, Icon, FieldError, FormError],
  template: `
    <div class="section-head">
      <div><h2>Типы объектов</h2><small>Задают геометрию фиксаций и схему дополнительных атрибутов.</small></div>
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
          <p class="empty">Типов объектов пока нет. Создайте первый.</p>
        } @else {
          <table class="table">
            <thead><tr><th>Название</th><th>Категория</th><th>Геометрия</th><th></th></tr></thead>
            <tbody>
              @for (t of rows(); track t.id) {
                <tr class="is-clickable" [class.is-active]="draft()?.id === t.id" (click)="edit(t)">
                  <td><strong>{{ t.name }}</strong><br /><small>{{ t.code }}</small></td>
                  <td>{{ categoryName(t.categoryId) }}</td>
                  <td><span class="chip">{{ geometryLabel(t.geometryKind) }}</span></td>
                  <td class="actions">
                    <button class="btn btn--ghost btn--sm" type="button" (click)="edit(t); $event.stopPropagation()"><app-icon name="edit" />Изменить</button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        }
      </div>

      @if (draft(); as d) {
        <form class="card stack" (submit)="save($event)" aria-label="Тип объекта">
          <h2>{{ d.id ? 'Тип объекта' : 'Новый тип объекта' }}</h2>

          <div class="field">
            <label for="ot-category">Категория</label>
            <select id="ot-category" name="categoryId" [ngModel]="d.categoryId" (ngModelChange)="patch({ categoryId: $event })">
              <option value="">Выберите категорию</option>
              @for (c of categories(); track c.id) { <option [value]="c.id">{{ c.name }}</option> }
            </select>
            @if (!categories().length && !loading()) { <span class="hint">Сначала создайте категорию.</span> }
            <app-field-error [error]="formError()" field="categoryId" />
          </div>

          <div class="field">
            <label for="ot-name">Название</label>
            <input id="ot-name" name="name" autocomplete="off" [ngModel]="d.name" (ngModelChange)="patch({ name: $event })" />
            @if (nameError()) { <p class="err">{{ nameError() }}</p> }
            <app-field-error [error]="formError()" field="name" />
          </div>

          <div class="field">
            <label for="ot-code">Код</label>
            <input id="ot-code" name="code" autocomplete="off" spellcheck="false" [ngModel]="d.code" (ngModelChange)="patch({ code: $event })" />
            <app-field-error [error]="formError()" field="code" />
          </div>

          <div class="field">
            <label for="ot-geometry">Геометрия</label>
            <select id="ot-geometry" name="geometryKind" [ngModel]="d.geometryKind" (ngModelChange)="patch({ geometryKind: $event })">
              @for (g of geometry; track g.value) { <option [value]="g.value">{{ g.label }}</option> }
            </select>
            <app-field-error [error]="formError()" field="geometryKind" />
          </div>

          <div class="field">
            <label for="ot-schema">Схема атрибутов (JSON)</label>
            <textarea id="ot-schema" name="attributesSchema" class="mono" spellcheck="false"
              [ngModel]="d.schemaText" (ngModelChange)="patch({ schemaText: $event })"
              placeholder='{"type":"object","properties":{}}'></textarea>
            @if (schemaError()) { <p class="err">{{ schemaError() }}</p> }
            <app-field-error [error]="formError()" field="attributesSchema" />
            <div class="row">
              <button class="btn btn--ghost btn--sm" type="button" (click)="insertExample()">Вставить пример</button>
              <span class="hint">JSON-объект; пустое поле означает схему без атрибутов.</span>
            </div>
          </div>

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
        <div class="card empty">Выберите тип в таблице или создайте новый.</div>
      }
    </div>`,
  styles: `
    .pad { padding: var(--pad); }
    .err { font-size: 12px; color: var(--st-none); }
    small { color: var(--ink-3); }
    .mono { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 13px; min-height: 150px; }
  `,
})
export class CatalogObjectTypes {
  private readonly api = inject(ObjectTypesApi);
  private readonly categoriesApi = inject(CategoriesApi);
  private readonly toasts = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  protected readonly geometry = GEOMETRY;
  protected readonly fields = ['categoryId', 'code', 'name', 'geometryKind', 'attributesSchema'];
  protected readonly text = errorText;

  protected readonly items = signal<ObjectType[]>([]);
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

  protected readonly schemaError = computed(() => {
    const d = this.draft();
    if (!d) return '';
    const parsed = parseAttributesSchema(d.schemaText);
    return parsed.ok ? '' : parsed.error;
  });

  protected readonly canSave = computed(() => {
    const d = this.draft();
    return !!d && !this.saving() && !!d.categoryId && !!d.code.trim() && !this.nameError() && !this.schemaError();
  });

  constructor() {
    void this.load();
  }

  protected async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const [types, categories] = await Promise.all([this.api.list(), this.categoriesApi.list()]);
      this.items.set(types);
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
      categoryId: this.categories()[0]?.id ?? '',
      code: '',
      name: '',
      geometryKind: 'any',
      schemaText: '',
    });
  }

  protected edit(type: ObjectType): void {
    this.formError.set(null);
    this.draft.set({
      id: type.id,
      categoryId: type.categoryId,
      code: type.code,
      name: type.name,
      geometryKind: type.geometryKind,
      schemaText: type.attributesSchema ? JSON.stringify(type.attributesSchema, null, 2) : '',
    });
  }

  protected patch(part: Partial<Draft>): void {
    this.formError.set(null);
    this.draft.update((d) => (d ? { ...d, ...part } : d));
  }

  protected insertExample(): void {
    this.patch({ schemaText: EXAMPLE_SCHEMA });
  }

  protected categoryName(id: string): string {
    return this.categoryNames().get(id) ?? '—';
  }

  protected geometryLabel(kind: GeometryKind): string {
    return GEOMETRY.find((g) => g.value === kind)?.label ?? kind;
  }

  protected async save(event: Event): Promise<void> {
    event.preventDefault();
    const d = this.draft();
    if (!d || !this.canSave()) return;

    const schema = parseAttributesSchema(d.schemaText);
    if (!schema.ok) return;

    const body: ObjectTypeInput = {
      categoryId: d.categoryId,
      code: d.code.trim(),
      name: d.name.trim(),
      geometryKind: d.geometryKind,
      attributesSchema: schema.value,
    };

    this.saving.set(true);
    this.formError.set(null);
    try {
      if (d.id) {
        const updated = await this.api.update(d.id, body);
        this.items.update((list) => list.map((item) => (item.id === updated.id ? updated : item)));
        this.toasts.success('Тип объекта сохранён');
      } else {
        const created = await this.api.create(body);
        this.items.update((list) => [...list, created]);
        this.toasts.success('Тип объекта создан');
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
      title: `Удалить тип «${name}»?`,
      text: 'Тип исчезнет из форм создания фиксаций. Если он используется, удаление не пройдёт.',
      confirmLabel: 'Удалить',
      danger: true,
    });
    if (!answer.confirmed) return;

    try {
      await this.api.remove(id);
      this.items.update((list) => list.filter((item) => item.id !== id));
      this.draft.update((d) => (d?.id === id ? null : d));
      this.toasts.success('Тип объекта удалён');
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
