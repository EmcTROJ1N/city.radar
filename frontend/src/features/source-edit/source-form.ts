import { Component, computed, effect, inject, input, output, signal } from '@angular/core';
import {
  AbstractControl,
  FormArray,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  Validators,
} from '@angular/forms';
import { SourcesApi } from '@entities/source';
import { StatusDefinitionsApi } from '@entities/status-definition';
import { ApiError, toApiError } from '@shared/api/api-error';
import type { Source, SourceType, StatusColor, StatusDefinition } from '@shared/api/types';
import { slugCode } from '@shared/lib/code';
import { FieldError } from '@shared/ui/field-error';
import { FormError } from '@shared/ui/form-error';
import { Hint } from '@shared/ui/hint';
import { Icon, normalizeIcon, type IconName } from '@shared/ui/icon';
import { StatusBadge } from '@shared/ui/status-badge';
import { ToastService } from '@shared/ui/toast';
import {
  SOURCE_TYPE_OPTIONS,
  STATUS_COLOR_OPTIONS,
  STATUS_ICON_OPTIONS,
  SourceFormValue,
  SourceStatusFormValue,
  TIMEOUT_UNITS,
  TimeoutUnit,
  emptySourceForm,
  emptyStatus,
  formToCreate,
  formToUpdate,
  sourceToForm,
} from './source-form.model';

interface StatusControls {
  code: FormControl<string>;
  name: FormControl<string>;
  description: FormControl<string>;
  color: FormControl<StatusColor>;
  icon: FormControl<IconName>;
  statusDefinitionId: FormControl<string | null>;
}

interface SourceControls {
  name: FormControl<string>;
  sourceType: FormControl<SourceType>;
  chatId: FormControl<string>;
  timeoutAmount: FormControl<number>;
  timeoutUnit: FormControl<TimeoutUnit>;
  userPrompt: FormControl<string>;
  linkByText: FormControl<boolean>;
  confidenceThreshold: FormControl<number | null>;
  keywords: FormControl<string>;
  regex: FormControl<string>;
  statuses: FormArray<FormGroup<StatusControls>>;
}

const API_FIELDS = [
  'name',
  'sourceType',
  'chatId',
  'timeoutSeconds',
  'confidenceThreshold',
  'prefilter',
  'prefilter.keywords',
  'prefilter.regex',
  'userPrompt',
  'linkByText',
];

/** Форма источника: создание вместе со статусами, редактирование — без них. */
@Component({
  selector: 'app-source-form',
  imports: [ReactiveFormsModule, FieldError, FormError, Hint, Icon, StatusBadge],
  template: `
    <form class="card stack" [formGroup]="form" (ngSubmit)="submit()" aria-label="Форма источника">
      <h2>{{ creating() ? 'Новый источник' : 'Источник' }}</h2>

      <div class="field">
        <label for="sf-name">Название</label>
        <input id="sf-name" formControlName="name" autocomplete="off" />
        <app-field-error [error]="error()" field="name" />
        @if (localError(f.name); as message) { <span class="err">{{ message }}</span> }
      </div>

      <div class="row">
        <div class="field grow">
          <label for="sf-type">Тип</label>
          @if (isSite()) {
            <span class="sys">Системный источник «Сайт» — создаётся вместе с категорией.</span>
          } @else {
            <select id="sf-type" formControlName="sourceType">
              @for (option of types; track option.value) { <option [value]="option.value">{{ option.label }}</option> }
            </select>
            @if (f.sourceType.value === 'telegram_chat') {
              <span class="hint">Сообщения читает Telegram-бот: добавьте его в этот чат.</span>
            }
          }
          <app-field-error [error]="error()" field="sourceType" />
        </div>
        <div class="field grow timeout">
          <label for="sf-timeout">Таймаут актуальности</label>
          <div class="row">
            <input id="sf-timeout" class="grow" type="number" min="1" step="1" formControlName="timeoutAmount" />
            <select aria-label="Единица таймаута" formControlName="timeoutUnit">
              @for (unit of units; track unit.value) { <option [value]="unit.value">{{ unit.label }}</option> }
            </select>
          </div>
          <span class="hint">Минимум 60 секунд.</span>
          <app-field-error [error]="error()" field="timeoutSeconds" />
          @if (localError(f.timeoutAmount, 'Укажите таймаут'); as message) { <span class="err">{{ message }}</span> }
        </div>
      </div>

      @if (!isSite()) {
        <div class="field">
          <label for="sf-chat">Идентификатор чата</label>
          <input id="sf-chat" formControlName="chatId" autocomplete="off" [placeholder]="chatHint()" />
          <span class="hint">{{ chatHint() }}</span>
          <app-field-error [error]="error()" field="chatId" />
          @if (localError(f.chatId); as message) { <span class="err">{{ message }}</span> }
        </div>
      }

      @if (creating()) {
        <fieldset class="st-block">
          <legend>Статусы, которые может докладывать источник</legend>

          <div class="row">
            <button class="btn btn--sm" type="button" (click)="addStatus()"><app-icon name="plus" />Добавить статус</button>
            <select class="dict" aria-label="Добавить статус из справочника" (change)="pickDefinition($event)">
              <option value="">Из справочника</option>
              @for (definition of definitions(); track definition.id) {
                <option [value]="definition.id">{{ definition.name }}</option>
              }
            </select>
            @if (definitionsError()) {
              <button class="btn btn--ghost btn--sm" type="button" (click)="loadDefinitions()">Справочник недоступен — повторить</button>
            }
          </div>

          @for (row of statusRows; track row; let i = $index) {
            <div class="stat">
              <div class="row">
                <input class="grow" [formControl]="row.controls.name" (input)="syncCode(row, $any($event.target).value)" placeholder="Название, например «Вода есть»" aria-label="Название статуса" />
                <input class="code" [formControl]="row.controls.code" placeholder="ok" aria-label="Код статуса" />
                <button class="btn btn--ghost btn--icon btn--danger" type="button" aria-label="Удалить статус" (click)="removeStatus(i)">
                  <app-icon name="trash" />
                </button>
              </div>
              <div class="row">
                <select [formControl]="row.controls.color" aria-label="Цвет статуса">
                  @for (option of colors; track option.value) { <option [value]="option.value">{{ option.label }}</option> }
                </select>
                <app-status-badge
                  [label]="row.controls.name.value || 'Статус'"
                  [tone]="row.controls.color.value"
                  [icon]="row.controls.icon.value"
                />
                <select [formControl]="row.controls.icon" aria-label="Иконка статуса">
                  @for (option of icons; track option.value) { <option [value]="option.value">{{ option.label }}</option> }
                </select>
              </div>
              <input [formControl]="row.controls.description" placeholder="Что означает (подсказка для LLM)" aria-label="Описание статуса" />
              @if (localError(row.controls.name); as message) { <span class="err">{{ message }}</span> }
              @if (localError(row.controls.code); as message) { <span class="err">{{ message }}</span> }
              @if (localError(row.controls.description); as message) { <span class="err">{{ message }}</span> }
              <app-field-error [error]="error()" [field]="'statuses[' + i + '].name'" />
              <app-field-error [error]="error()" [field]="'statuses[' + i + '].code'" />
              <app-field-error [error]="error()" [field]="'statuses[' + i + '].description'" />
            </div>
          }

          @if (form.controls.statuses.touched && !statusRows.length) {
            <span class="err">Добавьте хотя бы один статус</span>
          }
        </fieldset>
      }

      @if (!isSite()) {
        <div class="field">
          <label for="sf-prompt">Промпт-фильтр</label>
          <textarea id="sf-prompt" formControlName="userPrompt"
            placeholder="Ищи в чате все упоминания про подачу и прекращение подачи воды"></textarea>
          <span class="hint">К промпту автоматически добавляется системный: контекст задачи, список статусов и формат ответа. Его нельзя изменить.</span>
          <app-field-error [error]="error()" field="userPrompt" />
          @if (localError(f.userPrompt); as message) { <span class="err">{{ message }}</span> }
        </div>

        <div class="row link-row">
          <label class="row">
            <input type="checkbox" formControlName="linkByText" />
            Определять фиксацию по тексту сообщения
          </label>
          <app-hint text="Для чата, который пишет про несколько мест. Включено: статус получит только место, названное в сообщении. Выключено: все привязанные места. Для одного места не влияет." />
        </div>

        <details class="more">
          <summary>Дополнительно</summary>
          <div class="stack">
            <div class="field">
              <label for="sf-confidence">Порог уверенности</label>
              <input id="sf-confidence" type="number" min="0" max="1" step="0.05" formControlName="confidenceThreshold" />
              <span class="hint">От 0 до 1. Пусто — значение из глобальных настроек.</span>
              <app-field-error [error]="error()" field="confidenceThreshold" />
              @if (localError(f.confidenceThreshold); as message) { <span class="err">{{ message }}</span> }
            </div>
            <div class="field">
              <label for="sf-keywords">Ключевые слова</label>
              <input id="sf-keywords" formControlName="keywords" placeholder="вода, отключение, колонка" />
              <span class="hint">Через запятую. Дешёвый префильтр до обращения к LLM.</span>
              <app-field-error [error]="error()" field="prefilter.keywords" />
            </div>
            <div class="field">
              <label for="sf-regex">Регулярные выражения</label>
              <textarea id="sf-regex" formControlName="regex" placeholder="вод[аы]"></textarea>
              <span class="hint">По одному выражению на строку.</span>
              <app-field-error [error]="error()" field="prefilter.regex" />
            </div>
          </div>
        </details>
      }

      <app-form-error [error]="error()" [fields]="apiFields" />

      <div class="row">
        <button class="btn btn--primary" type="submit" [disabled]="saving()">
          {{ saving() ? 'Сохранение…' : 'Сохранить' }}
        </button>
        <button class="btn btn--ghost" type="button" (click)="cancelled.emit()">Закрыть</button>
      </div>
    </form>`,
  styles: `
    .st-block { display: grid; gap: 12px; margin: 0; padding: 12px; border: 1px solid var(--line); border-radius: var(--r-md); }
    legend { padding: 0 6px; font-size: 13px; font-weight: 500; color: var(--ink-2); }
    .stat { display: grid; gap: 6px; }
    .stat input, .stat select, .dict { height: var(--control-h); padding: 0 10px; border: 1px solid var(--line); border-radius: var(--r-md); background: var(--surface); }
    .code { width: 130px; }
    .timeout .row { flex-wrap: nowrap; }
    .more summary { cursor: pointer; font-weight: 500; color: var(--ink-2); }
    .more .stack { margin-top: 10px; }
    .link-row { flex-wrap: nowrap; }
    .sys { display: inline-flex; align-items: center; min-height: var(--control-h); color: var(--ink-2); font-size: 14px; }
    .err { color: var(--st-none); font-size: 12px; }
  `,
})
export class SourceForm {
  private readonly api = inject(SourcesApi);
  private readonly definitionsApi = inject(StatusDefinitionsApi);
  private readonly toasts = inject(ToastService);

  readonly source = input<Source | null>(null);
  readonly saved = output<Source>();
  readonly cancelled = output<void>();

  protected readonly types = SOURCE_TYPE_OPTIONS;
  protected readonly units = TIMEOUT_UNITS;
  protected readonly colors = STATUS_COLOR_OPTIONS;
  protected readonly icons = STATUS_ICON_OPTIONS;
  protected readonly apiFields = API_FIELDS;

  protected readonly form = new FormGroup<SourceControls>({
    name: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    sourceType: new FormControl<SourceType>('telegram_chat', { nonNullable: true, validators: [Validators.required] }),
    chatId: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    timeoutAmount: new FormControl(6, { nonNullable: true, validators: [Validators.required, Validators.min(1)] }),
    timeoutUnit: new FormControl<TimeoutUnit>('hour', { nonNullable: true, validators: [Validators.required] }),
    userPrompt: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    linkByText: new FormControl(true, { nonNullable: true }),
    confidenceThreshold: new FormControl<number | null>(null, [Validators.min(0), Validators.max(1)]),
    keywords: new FormControl('', { nonNullable: true }),
    regex: new FormControl('', { nonNullable: true }),
    statuses: new FormArray<FormGroup<StatusControls>>([]),
  });

  protected readonly f = this.form.controls;
  protected readonly creating = computed(() => this.source() === null);
  protected readonly isSite = computed(() => this.source()?.sourceType === 'site');
  protected readonly error = signal<ApiError | null>(null);
  protected readonly saving = signal(false);
  protected readonly definitions = signal<StatusDefinition[]>([]);
  protected readonly definitionsError = signal(false);

  protected readonly chatHint = computed(() =>
    this.f.sourceType.value === 'telegram_chat'
      ? 'Telegram — @username, числовой ID (например, -1001234567890) или ссылка t.me/…'
      : 'MAX — идентификатор чата MAX',
  );

  private currentId: string | null | undefined;
  private definitionsLoaded = false;

  constructor() {
    effect(() => {
      const source = this.source();
      const key = source?.id ?? null;
      if (key === this.currentId) return;
      this.currentId = key;
      this.fill(source);
      if (!source) this.loadDefinitions();
    });
  }

  protected get statusRows(): FormGroup<StatusControls>[] {
    return this.form.controls.statuses.controls;
  }

  protected addStatus(value: SourceStatusFormValue = emptyStatus()): void {
    const group = this.newStatus(value);
    this.form.controls.statuses.push(group);
  }

  protected removeStatus(index: number): void {
    this.form.controls.statuses.removeAt(index);
  }

  protected pickDefinition(event: Event): void {
    const select = event.target as HTMLSelectElement;
    const id = select.value;
    select.value = '';
    const definition = this.definitions().find((item) => item.id === id);
    if (!definition) return;
    const group = this.newStatus({
      code: definition.code,
      name: definition.name,
      description: '',
      color: definition.color,
      icon: normalizeIcon(definition.icon),
      statusDefinitionId: definition.id,
    });
    group.controls.code.markAsDirty();
    this.form.controls.statuses.push(group);
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

  protected syncCode(row: FormGroup<StatusControls>, name: string): void {
    if (row.controls.code.pristine) {
      row.controls.code.setValue(slugCode(name), { emitEvent: false });
    }
  }

  protected localError(control: AbstractControl | null, required = 'Заполните поле'): string {
    if (!control || !control.touched) return '';
    if (control.hasError('required')) return required;
    if (control.hasError('pattern')) return 'Только латинские буквы, цифры и подчёркивание';
    if (control.hasError('min')) return 'Слишком маленькое значение';
    if (control.hasError('max')) return 'Слишком большое значение';
    return '';
  }

  protected async submit(): Promise<void> {
    this.form.markAllAsTouched();
    this.error.set(null);
    const source = this.source();
    if (this.form.invalid || this.saving()) return;
    if (!source && !this.statusRows.length) return;

    this.saving.set(true);
    try {
      const raw = this.form.getRawValue();
      const result = source
        ? await this.api.update(source.id, formToUpdate(raw))
        : await this.api.create(formToCreate(raw));
      this.toasts.success(source ? 'Источник сохранён' : 'Источник создан');
      this.saved.emit(result);
    } catch (err) {
      this.error.set(toApiError(err));
    } finally {
      this.saving.set(false);
    }
  }

  private fill(source: Source | null): void {
    this.error.set(null);
    this.form.reset();
    this.form.patchValue(source ? sourceToForm(source) : emptySourceForm());
    this.applySourceValidators(source?.sourceType === 'site');
    this.form.controls.statuses.clear();
    if (!source) this.addStatus();
  }

  /**
   * У системного «Сайта» нет чата и промпта: скрытые поля не должны блокировать сохранение
   * названия и таймаута.
   */
  private applySourceValidators(site: boolean): void {
    const chat = this.form.controls.chatId;
    const prompt = this.form.controls.userPrompt;

    if (site) {
      chat.clearValidators();
      prompt.clearValidators();
    } else {
      chat.setValidators([Validators.required]);
      prompt.setValidators([Validators.required]);
    }

    chat.updateValueAndValidity({ emitEvent: false });
    prompt.updateValueAndValidity({ emitEvent: false });
  }

  private newStatus(value: SourceStatusFormValue): FormGroup<StatusControls> {
    const group = new FormGroup<StatusControls>({
      code: new FormControl(value.code, {
        nonNullable: true,
        validators: [Validators.required, Validators.pattern(/^[a-z0-9_]+$/)],
      }),
      name: new FormControl(value.name, { nonNullable: true, validators: [Validators.required] }),
      description: new FormControl(value.description, { nonNullable: true, validators: [] }),
      color: new FormControl<StatusColor>(value.color, { nonNullable: true, validators: [Validators.required] }),
      icon: new FormControl<IconName>(value.icon, { nonNullable: true, validators: [Validators.required] }),
      statusDefinitionId: new FormControl<string | null>(value.statusDefinitionId),
    });
    return group;
  }
}
