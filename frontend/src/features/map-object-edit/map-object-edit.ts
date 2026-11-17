import { Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { CatalogObjectsApi } from '@entities/map-object';
import { SourcesApi } from '@entities/source';
import { ApiError, errorText, toApiError } from '@shared/api/api-error';
import type { CoverageKind, MapObject, ObjectGroup, ObjectType } from '@shared/api/types';
import { SOURCE_TYPE_LABEL } from '@shared/lib/labels';
import type { DrawMode, MapGeometry } from '@shared/map-adapter/map-adapter';
import { ConfirmService } from '@shared/ui/confirm';
import { EntityPicker, PickerLoader } from '@shared/ui/entity-picker';
import { FieldError } from '@shared/ui/field-error';
import { FormError } from '@shared/ui/form-error';
import { Icon } from '@shared/ui/icon';
import { ToastService } from '@shared/ui/toast';
import {
  COVERAGE,
  MapObjectFormValue,
  SourceDiff,
  emptyMapObjectForm,
  formFromObject,
  geometryMismatch,
  inputFromForm,
  polygonVertexCount,
  sourceDiff,
} from './map-object-form.model';

const SHOWN_FIELDS = [
  'name',
  'objectTypeId',
  'groupId',
  'coverageKind',
  'geometry',
  'address',
  'isActive',
  'sourceIds',
];

function formatCoordinate(value: number): string {
  return String(Number(value.toFixed(6)));
}

/** Форма фиксации: поля, геометрия на карте и привязка источников. */
@Component({
  selector: 'app-map-object-edit',
  imports: [ReactiveFormsModule, EntityPicker, FieldError, FormError, Icon],
  template: `
    <form class="card stack" [formGroup]="form" (ngSubmit)="save()" aria-label="Редактирование фиксации">
      <h2>{{ isNew() ? 'Новая фиксация' : 'Фиксация' }}</h2>

      <div class="field">
        <label for="mo-name">Название</label>
        <input id="mo-name" formControlName="name" required autocomplete="off" />
        @if (form.controls.name.touched && form.controls.name.hasError('required')) {
          <span class="err">Укажите название</span>
        }
        <app-field-error [error]="apiError()" field="name" />
      </div>

      <div class="field">
        <label>Вид покрытия</label>
        <div class="segmented" role="group" aria-label="Вид покрытия">
          @for (option of coverage; track option.value) {
            <button type="button" [attr.aria-pressed]="coverageKind() === option.value" (click)="setKind(option.value)">
              {{ option.label }}
            </button>
          }
        </div>
        <app-field-error [error]="apiError()" field="coverageKind" />
      </div>

      <section class="geometry stack" aria-label="Геометрия на карте">
        <p class="hint">{{ geometryHint() }}</p>
        <div class="row">
          <button class="btn btn--sm" type="button" [disabled]="coverageKind() === 'point'" (click)="undoRequest.emit()">
            <app-icon name="edit" />Отменить вершину
          </button>
          <button class="btn btn--sm btn--ghost" type="button" (click)="clearGeometry()">
            <app-icon name="cross" />Очистить
          </button>
          @if (coverageKind() === 'polygon') {
            <span class="hint">Вершин: {{ vertices() }}@if (vertices() < 3) { · нужно минимум 3 }</span>
          }
        </div>

        @if (coverageKind() === 'point') {
          <div class="row">
            <div class="field grow">
              <label for="mo-lon">Долгота</label>
              <input id="mo-lon" inputmode="decimal" [value]="lon()" (input)="onLon($any($event.target).value)" placeholder="37.800000" />
            </div>
            <div class="field grow">
              <label for="mo-lat">Широта</label>
              <input id="mo-lat" inputmode="decimal" [value]="lat()" (input)="onLat($any($event.target).value)" placeholder="48.010000" />
            </div>
          </div>
        }
        <app-field-error [error]="apiError()" field="geometry" />
      </section>

      <div class="field">
        <label for="mo-type">Тип объекта</label>
        <select id="mo-type" [value]="objectTypeId()" (change)="setTypeId($any($event.target).value)">
          <option value="">Не задан</option>
          @for (type of objectTypes(); track type.id) { <option [value]="type.id">{{ type.name }}</option> }
        </select>
        <app-field-error [error]="apiError()" field="objectTypeId" />
      </div>

      @if (typeMismatch()) {
        <p class="callout" role="status">
          <app-icon name="alert" />
          <span>Тип рассчитан на {{ kindName(selectedType()?.geometryKind) }}, а выбран вид «{{ coverageLabel(coverageKind()) }}».</span>
        </p>
      }

      <div class="field">
        <label for="mo-group">Группа</label>
        <select id="mo-group" [value]="groupId()" (change)="setGroupId($any($event.target).value)">
          <option value="">Без группы</option>
          @for (group of groups(); track group.id) { <option [value]="group.id">{{ group.name }}</option> }
        </select>
        <app-field-error [error]="apiError()" field="groupId" />
      </div>

      <div class="field">
        <label for="mo-address">Адрес</label>
        <input id="mo-address" formControlName="address" autocomplete="off" />
        <app-field-error [error]="apiError()" field="address" />
      </div>

      <div class="field">
        <label>Источники данных</label>
        <app-entity-picker
          [multiple]="true"
          [loader]="sourceLoader"
          [labels]="sourceLabels()"
          [(value)]="sourceIds"
          placeholder="Выберите источники"
        />
        @if (!sourceIds().length) {
          <span class="hint">Без источников фиксацию нельзя отметить на карте.</span>
        }
        <app-field-error [error]="apiError()" field="sourceIds" />
      </div>

      <label class="row checkbox"><input type="checkbox" formControlName="isActive" />Фиксация активна</label>
      <app-field-error [error]="apiError()" field="isActive" />

      <app-form-error [error]="apiError()" [fields]="shownFields" />

      <div class="row actions">
        <button class="btn btn--primary" type="submit" [disabled]="saving() || form.invalid || !geometry()">
          {{ saving() ? 'Сохранение…' : 'Сохранить' }}
        </button>
        <button class="btn btn--ghost" type="button" (click)="closed.emit()">Закрыть</button>
        @if (!isNew()) {
          <button class="btn btn--ghost btn--danger" type="button" (click)="remove()">
            <app-icon name="trash" />Удалить
          </button>
        }
      </div>
    </form>`,
  styles: `
    :host { display: block; }
    .geometry { padding: 10px; border: 1px dashed var(--line); border-radius: var(--r-md); }
    .checkbox { gap: 6px; font-size: 13px; }
    .actions { padding-top: 4px; }
    input[type='checkbox'] { width: 16px; height: 16px; }
  `,
})
export class MapObjectEdit {
  private readonly api = inject(CatalogObjectsApi);
  private readonly sources = inject(SourcesApi);
  private readonly toasts = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  private readonly fb = inject(NonNullableFormBuilder);

  readonly object = input<MapObject | null>(null);
  readonly isNew = input(false);
  readonly objectTypes = input<ObjectType[]>([]);
  readonly groups = input<ObjectGroup[]>([]);

  readonly saved = output<MapObject>();
  readonly removed = output<void>();
  readonly closed = output<void>();
  readonly undoRequest = output<void>();
  readonly clearRequest = output<void>();

  readonly coverage = COVERAGE;
  readonly shownFields = SHOWN_FIELDS;

  readonly drawMode = signal<DrawMode>('none');
  readonly drawInitial = signal<MapGeometry | null>(null);
  readonly coverageKind = signal<CoverageKind>('polygon');
  readonly geometry = signal<MapGeometry | null>(null);
  readonly sourceIds = signal<string[]>([]);
  readonly sourceLabels = signal<Record<string, string>>({});
  readonly apiError = signal<ApiError | null>(null);
  readonly saving = signal(false);
  readonly lon = signal('');
  readonly lat = signal('');

  readonly form = this.fb.group({
    name: ['', [Validators.required]],
    address: [''],
    objectTypeId: [''],
    groupId: [''],
    isActive: [true],
  });

  protected readonly objectTypeId = toSignal(this.form.controls.objectTypeId.valueChanges, {
    initialValue: '',
  });
  protected readonly groupId = toSignal(this.form.controls.groupId.valueChanges, { initialValue: '' });

  readonly selectedType = computed(
    () => this.objectTypes().find((type) => type.id === this.objectTypeId()) ?? null,
  );
  readonly typeMismatch = computed(() => geometryMismatch(this.selectedType(), this.coverageKind()));
  readonly vertices = computed(() => polygonVertexCount(this.geometry()));
  readonly geometryHint = computed(() =>
    this.coverageKind() === 'point'
      ? 'Кликните по карте, чтобы поставить точку'
      : 'Кликайте, чтобы добавить вершины; двойной клик — завершить',
  );

  readonly sourceLoader: PickerLoader = async (query, cursor) => {
    const page = await this.sources.list({
      q: query || undefined,
      cursor: cursor ?? undefined,
      limit: 20,
    });
    return {
      items: page.items
        // «Сайт» доступен фиксациям неявно: привязывать его нельзя.
        .filter((source) => source.sourceType !== 'site')
        .map((source) => ({
          value: source.id,
          label: source.name,
          hint: `${SOURCE_TYPE_LABEL[source.sourceType]} · ${source.chatId}`,
        })),
      nextCursor: page.nextCursor,
    };
  };

  private originalSourceIds: string[] = [];

  constructor() {
    effect(() => {
      const object = this.object();
      const isNew = this.isNew();
      this.apply(object, isNew);
    });
  }

  onDrawn(geometry: MapGeometry | null): void {
    this.geometry.set(geometry);
    if (geometry?.type === 'Point') this.setLonLat(geometry);
    else {
      this.lon.set('');
      this.lat.set('');
    }
  }

  protected async setKind(kind: CoverageKind): Promise<void> {
    if (kind === this.coverageKind()) return;
    if (this.geometry()) {
      const result = await this.confirm.ask({
        title: 'Сменить вид покрытия?',
        text: 'Нарисованная геометрия будет сброшена.',
        confirmLabel: 'Сбросить',
        danger: true,
      });
      if (!result.confirmed) return;
    }
    this.coverageKind.set(kind);
    this.geometry.set(null);
    this.drawInitial.set(null);
    this.drawMode.set(kind);
    this.lon.set('');
    this.lat.set('');
  }

  protected setTypeId(value: string): void {
    this.form.controls.objectTypeId.setValue(value);
  }

  protected setGroupId(value: string): void {
    this.form.controls.groupId.setValue(value);
  }

  protected coverageLabel(kind: CoverageKind): string {
    return COVERAGE.find((option) => option.value === kind)?.label ?? kind;
  }

  protected kindName(kind: string | undefined): string {
    if (kind === 'point') return 'точку';
    if (kind === 'polygon') return 'геозону';
    return 'любой вид';
  }

  protected onLon(raw: string): void {
    this.lon.set(raw);
    this.syncManualPoint();
  }

  protected onLat(raw: string): void {
    this.lat.set(raw);
    this.syncManualPoint();
  }

  protected clearGeometry(): void {
    this.geometry.set(null);
    this.drawInitial.set(null);
    this.lon.set('');
    this.lat.set('');
    this.clearRequest.emit();
  }

  async save(): Promise<void> {
    if (this.saving()) return;
    this.form.markAllAsTouched();
    const geometry = this.geometry();
    if (this.form.invalid || !geometry) return;

    const value: MapObjectFormValue = {
      name: this.form.controls.name.value,
      coverageKind: this.coverageKind(),
      geometry,
      objectTypeId: this.form.controls.objectTypeId.value || null,
      groupId: this.form.controls.groupId.value || null,
      address: this.form.controls.address.value,
      isActive: this.form.controls.isActive.value,
      sourceIds: this.sourceIds(),
    };

    this.saving.set(true);
    this.apiError.set(null);
    try {
      if (this.isNew() || !this.object()) {
        const created = await this.api.create(inputFromForm(value, { withSources: true }));
        this.originalSourceIds = [...value.sourceIds];
        this.toasts.success('Фиксация создана');
        this.saved.emit(created);
        return;
      }

      const current = this.object()!;
      const updated = await this.api.update(
        current.id,
        inputFromForm(value, { withSources: false }),
      );
      const failures = await this.syncSources(current.id, sourceDiff(this.originalSourceIds, value.sourceIds));
      this.originalSourceIds = [...value.sourceIds];
      this.saved.emit(updated);
      if (failures.length) {
        this.toasts.error(`Источники обновлены не полностью: ${failures.join('; ')}`);
      } else {
        this.toasts.success('Фиксация сохранена');
      }
    } catch (err) {
      const error = toApiError(err);
      this.apiError.set(error);
      if (error.status !== 422) this.toasts.fromError(error);
    } finally {
      this.saving.set(false);
    }
  }

  async remove(): Promise<void> {
    const current = this.object();
    if (!current) return;
    const result = await this.confirm.ask({
      title: 'Удалить фиксацию?',
      text: `«${current.name}» будет удалена.`,
      confirmLabel: 'Удалить',
      danger: true,
    });
    if (!result.confirmed) return;

    try {
      await this.api.remove(current.id);
      this.toasts.success('Фиксация удалена');
      this.removed.emit();
    } catch (err) {
      const error = toApiError(err);
      if (error.status === 409) this.toasts.error(error.detail ?? 'Фиксацию нельзя удалить');
      else this.toasts.fromError(error);
    }
  }

  private async syncSources(id: string, diff: SourceDiff): Promise<string[]> {
    const failures: string[] = [];
    for (const sourceId of diff.toUnlink) {
      try {
        await this.api.unlinkSource(id, sourceId);
      } catch (err) {
        failures.push(`не удалось отвязать «${this.sourceName(sourceId)}»: ${errorText(toApiError(err))}`);
      }
    }
    for (const sourceId of diff.toLink) {
      try {
        await this.api.linkSource(id, sourceId);
      } catch (err) {
        failures.push(`не удалось привязать «${this.sourceName(sourceId)}»: ${errorText(toApiError(err))}`);
      }
    }
    return failures;
  }

  private sourceName(id: string): string {
    return this.sourceLabels()[id] ?? id;
  }

  private apply(object: MapObject | null, isNew: boolean): void {
    const value = !isNew && object ? formFromObject(object) : emptyMapObjectForm();
    this.originalSourceIds = [...value.sourceIds];
    this.sourceIds.set([...value.sourceIds]);
    this.coverageKind.set(value.coverageKind);
    this.geometry.set(value.geometry);
    this.drawInitial.set(value.geometry);
    this.drawMode.set(value.coverageKind);
    this.form.reset({
      name: value.name,
      address: value.address,
      objectTypeId: value.objectTypeId ?? '',
      groupId: value.groupId ?? '',
      isActive: value.isActive,
    });
    this.apiError.set(null);
    this.saving.set(false);
    this.setLonLat(value.geometry);
    if (value.sourceIds.length) void this.loadSourceLabels(value.sourceIds);
  }

  private setLonLat(geometry: MapGeometry | null): void {
    if (geometry?.type === 'Point') {
      this.lon.set(formatCoordinate(geometry.coordinates[0]));
      this.lat.set(formatCoordinate(geometry.coordinates[1]));
      return;
    }
    this.lon.set('');
    this.lat.set('');
  }

  private syncManualPoint(): void {
    const lon = Number(this.lon().replace(',', '.'));
    const lat = Number(this.lat().replace(',', '.'));
    if (!Number.isFinite(lon) || !Number.isFinite(lat)) return;
    if (!this.lon().trim() || !this.lat().trim()) return;
    const geometry: MapGeometry = { type: 'Point', coordinates: [lon, lat] };
    this.geometry.set(geometry);
    this.drawInitial.set(geometry);
  }

  private async loadSourceLabels(ids: string[]): Promise<void> {
    const known = this.sourceLabels();
    const missing = [...new Set(ids)].filter((id) => !known[id]);
    if (!missing.length) return;
    const results = await Promise.allSettled(missing.map((id) => this.sources.get(id)));
    const patch = { ...this.sourceLabels() };
    for (const result of results) {
      if (result.status === 'fulfilled') patch[result.value.id] = result.value.name;
    }
    this.sourceLabels.set(patch);
  }
}
