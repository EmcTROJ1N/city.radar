import { Component, effect, inject, input, output, signal } from '@angular/core';
import { SourcesApi } from '@entities/source';
import { StatusDefinitionsApi } from '@entities/status-definition';
import { ApiError, errorText, toApiError } from '@shared/api/api-error';
import type { Source, SourceStatus, StatusDefinition } from '@shared/api/types';
import { slugCode } from '@shared/lib/code';
import { ConfirmService } from '@shared/ui/confirm';
import { Icon, normalizeIcon } from '@shared/ui/icon';
import { StatusBadge } from '@shared/ui/status-badge';
import { ToastService } from '@shared/ui/toast';
import {
  STATUS_COLOR_OPTIONS,
  STATUS_ICON_OPTIONS,
  SourceStatusFormValue,
  emptyStatus,
  formToStatusInput,
  statusToForm,
} from './source-form.model';

interface StatusRow extends SourceStatusFormValue {
  key: number;
  id: string | null;
  codeTouched: boolean;
  touched: boolean;
  saving: boolean;
  error: ApiError | null;
}

let seq = 0;

/** Статусы существующего источника: каждая строка сохраняется отдельным запросом. Живёт во вкладке окна настроек. */
@Component({
  selector: 'app-source-statuses',
  imports: [Icon, StatusBadge],
  template: `
    <section class="stack" aria-labelledby="source-statuses-title">
      <div>
        <h2 id="source-statuses-title" class="visually-hidden">Статусы источника</h2>
        <p class="hint">Каждый статус сохраняется отдельно. Код уникален внутри источника.</p>
      </div>

      <div class="row toolbar">
        <button class="btn" type="button" (click)="addStatus()"><app-icon name="plus" />Добавить статус</button>
        <select class="dict" aria-label="Добавить статус из справочника" (change)="pickDefinition($event)">
          <option value="">Из справочника</option>
          @for (definition of definitions(); track definition.id) {
            <option [value]="definition.id">{{ definition.name }}</option>
          }
        </select>
        @if (definitionsError()) {
          <button class="btn btn--ghost" type="button" (click)="loadDefinitions()">Справочник недоступен — повторить</button>
        }
      </div>

      @for (row of rows(); track row.key) {
        <div class="status-row">
          <div class="row status-row__head">
            <app-status-badge [label]="row.name || 'Статус'" [tone]="row.color" [icon]="row.icon" />
            <span class="grow"></span>
            <button class="btn" type="button" [disabled]="row.saving" (click)="save(row)">
              <app-icon name="check" />{{ row.saving ? 'Сохранение…' : 'Сохранить' }}
            </button>
            <button class="btn btn--ghost btn--icon btn--danger" type="button" [attr.aria-label]="'Удалить статус ' + row.name"
              (click)="remove(row)"><app-icon name="trash" /></button>
          </div>

          <!-- Поля — ровной сеткой в две колонки: название и код, описание на всю ширину, цвет и иконка. -->
          <div class="status-grid">
            <div class="field">
              <label [for]="'st-name-' + row.key">Название</label>
              <input [id]="'st-name-' + row.key" [value]="row.name" (input)="rename(row, $any($event.target).value)"
                placeholder="Например, «Вода есть»" />
            </div>
            <div class="field">
              <label [for]="'st-code-' + row.key">Код</label>
              <input [id]="'st-code-' + row.key" [value]="row.code"
                (input)="patch(row, { code: $any($event.target).value, codeTouched: true })" placeholder="ok" />
            </div>
            <div class="field status-grid__wide">
              <label [for]="'st-desc-' + row.key">Что означает</label>
              <input [id]="'st-desc-' + row.key" [value]="row.description"
                (input)="patch(row, { description: $any($event.target).value })" placeholder="Подсказка для LLM" />
            </div>
            <div class="field">
              <label [for]="'st-color-' + row.key">Цвет</label>
              <select [id]="'st-color-' + row.key" [value]="row.color" (change)="patch(row, { color: $any($event.target).value })">
                @for (option of colors; track option.value) { <option [value]="option.value">{{ option.label }}</option> }
              </select>
            </div>
            <div class="field">
              <label [for]="'st-icon-' + row.key">Иконка</label>
              <select [id]="'st-icon-' + row.key" [value]="row.icon" (change)="patch(row, { icon: $any($event.target).value })">
                @for (option of icons; track option.value) { <option [value]="option.value">{{ option.label }}</option> }
              </select>
            </div>
          </div>

          @if (row.touched && !row.name.trim()) { <span class="err">Укажите название</span> }
          @if (row.touched && codeInvalid(row)) {
            <span class="err">Код: только латинские буквы, цифры и подчёркивание</span>
          }
          @if (row.error; as error) {
            @for (message of errorMessages(error); track message) { <span class="err">{{ message }}</span> }
          }
        </div>
      } @empty {
        <p class="empty">У источника пока нет статусов. Добавьте хотя бы один.</p>
      }
    </section>`,
  styles: `
    .dict { height: var(--control-h); padding: 0 34px 0 12px; border: 1px solid var(--line); border-radius: var(--r-md); background-color: var(--surface); }
    .status-row { display: grid; gap: 12px; padding: 12px; border: 1px solid var(--line-soft); border-radius: var(--r-md); }
    .status-row__head { flex-wrap: nowrap; }
    .status-row__head app-status-badge { min-width: 0; }
    .status-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 10px 12px; }
    .status-grid__wide { grid-column: 1 / -1; }
    @media (max-width: 520px) {
      .status-grid { grid-template-columns: minmax(0, 1fr); }
    }
    .hint { font-size: 12px; color: var(--ink-3); }
    .err { color: var(--st-none); font-size: 12px; }
  `,
})
export class SourceStatuses {
  private readonly api = inject(SourcesApi);
  private readonly definitionsApi = inject(StatusDefinitionsApi);
  private readonly toasts = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  readonly source = input.required<Source>();
  readonly statusesChange = output<SourceStatus[]>();

  protected readonly rows = signal<StatusRow[]>([]);
  protected readonly colors = STATUS_COLOR_OPTIONS;
  protected readonly icons = STATUS_ICON_OPTIONS;
  protected readonly definitions = signal<StatusDefinition[]>([]);
  protected readonly definitionsError = signal(false);

  private currentId: string | null = null;
  private definitionsLoaded = false;

  constructor() {
    effect(() => {
      const source = this.source();
      if (source.id === this.currentId) return;
      this.currentId = source.id;
      this.rows.set(source.statuses.map((status) => this.rowFromStatus(status)));
    });
    this.loadDefinitions();
  }

  protected loadDefinitions(): void {
    if (this.definitionsLoaded) return;
    this.definitionsError.set(false);
    this.definitionsApi
      .list()
      .then((items) => {
        this.definitions.set(items);
        this.definitionsLoaded = true;
      })
      .catch(() => {
        this.definitionsLoaded = false;
        this.definitionsError.set(true);
      });
  }

  protected addStatus(value: SourceStatusFormValue = emptyStatus()): void {
    this.rows.update((list) => [...list, this.rowFromValue(value)]);
  }

  protected pickDefinition(event: Event): void {
    const select = event.target as HTMLSelectElement;
    const id = select.value;
    select.value = '';
    const definition = this.definitions().find((item) => item.id === id);
    if (!definition) return;
    this.addStatus({
      code: definition.code,
      name: definition.name,
      description: '',
      color: definition.color,
      icon: normalizeIcon(definition.icon),
      statusDefinitionId: definition.id,
    });
  }

  protected rename(row: StatusRow, name: string): void {
    const code = row.id === null && !row.codeTouched ? slugCode(name) : row.code;
    this.patch(row, { name, code });
  }

  protected patch(row: StatusRow, changes: Partial<StatusRow>): void {
    this.rows.update((list) =>
      list.map((item) => (item.key === row.key ? { ...item, ...changes, error: null } : item)),
    );
  }

  protected codeInvalid(row: StatusRow): boolean {
    return !/^[a-z0-9_]+$/.test(row.code);
  }

  protected errorMessages(error: ApiError): string[] {
    const fields = Object.values(error.fieldErrors);
    return fields.length ? fields : [errorText(error)];
  }

  protected async save(row: StatusRow): Promise<void> {
    if (row.saving) return;
    if (!row.name.trim() || this.codeInvalid(row)) {
      this.patch(row, { touched: true });
      return;
    }
    const source = this.source();
    const index = this.rows().findIndex((item) => item.key === row.key);
    const body = formToStatusInput(row, index + 1);
    this.patch(row, { saving: true });
    try {
      const result = row.id
        ? await this.api.updateStatus(source.id, row.id, body)
        : await this.api.createStatus(source.id, body);
      const saved = this.rowFromStatus(result, row.key);
      this.rows.update((list) => list.map((item) => (item.key === row.key ? saved : item)));
      this.emit();
      this.toasts.success('Статус сохранён');
    } catch (err) {
      const error = toApiError(err);
      if (error.status === 409 || error.status === 422) this.patch(row, { error });
      else this.toasts.fromError(error);
    } finally {
      this.rows.update((list) =>
        list.map((item) => (item.key === row.key ? { ...item, saving: false } : item)),
      );
    }
  }

  protected async remove(row: StatusRow): Promise<void> {
    if (row.id === null) {
      this.rows.update((list) => list.filter((item) => item.key !== row.key));
      return;
    }
    const result = await this.confirm.ask({
      title: 'Удалить статус?',
      text: `«${row.name}» будет удалён из источника.`,
      confirmLabel: 'Удалить',
      danger: true,
    });
    if (!result.confirmed) return;

    try {
      await this.api.removeStatus(this.source().id, row.id);
      this.rows.update((list) => list.filter((item) => item.key !== row.key));
      this.emit();
      this.toasts.success('Статус удалён');
    } catch (err) {
      const error = toApiError(err);
      if (error.status === 409 || error.status === 422) this.patch(row, { error });
      else this.toasts.fromError(error);
    }
  }

  private emit(): void {
    const statuses: SourceStatus[] = this.rows()
      .filter((row): row is StatusRow & { id: string } => row.id !== null)
      .map((row, index) => {
        const description = row.description.trim();
        return {
          id: row.id,
          code: row.code,
          name: row.name,
          ...(description ? { description } : {}),
          color: row.color,
          icon: row.icon,
          sortOrder: index + 1,
          statusDefinitionId: row.statusDefinitionId,
        };
      });
    this.statusesChange.emit(statuses);
  }

  private rowFromValue(value: SourceStatusFormValue): StatusRow {
    return {
      key: ++seq,
      id: null,
      ...value,
      codeTouched: Boolean(value.code),
      touched: false,
      saving: false,
      error: null,
    };
  }

  private rowFromStatus(status: SourceStatus, key = ++seq): StatusRow {
    return {
      key,
      id: status.id,
      ...statusToForm(status),
      codeTouched: true,
      touched: false,
      saving: false,
      error: null,
    };
  }
}
