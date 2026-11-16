import { Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CatalogObjectsApi } from '@entities/map-object';
import { ModerationApi } from '@entities/moderation-case';
import { SourcesApi } from '@entities/source';
import { ApiError, toApiError } from '@shared/api/api-error';
import type {
  ApproveCaseRequest,
  ModerationCase,
  RejectCaseRequest,
  SourceStatus,
} from '@shared/api/types';
import { CASE_KIND_LABEL } from '@shared/lib/labels';
import { ConfirmService } from '@shared/ui/confirm';
import { EntityPicker, PickerLoader } from '@shared/ui/entity-picker';
import { FormError } from '@shared/ui/form-error';
import { Icon, normalizeIcon } from '@shared/ui/icon';
import { ToastService } from '@shared/ui/toast';

const PICKER_LIMIT = 20;

/** Решение по открытому кейсу модерации: принять с правкой или отклонить. */
@Component({
  selector: 'app-resolve-moderation-case',
  imports: [RouterLink, EntityPicker, FormError, Icon],
  template: `
    @if (item(); as c) {
      <section class="stack resolve">
        <h3>Решение</h3>

        @if (needsObject()) {
          <div class="field">
            <label>Фиксация</label>
            <app-entity-picker [loader]="objectLoader" [(value)]="mapObjectValue"
              placeholder="Выберите фиксацию источника" />
            <span class="hint">Сообщение не удалось привязать к фиксации — выберите её вручную.</span>
          </div>
          <p class="hint">
            Чтобы такие сообщения определялись автоматически,
            @if (c.sourceId) {
              <a [routerLink]="'/sources'" [queryParams]="{ id: c.sourceId }">дополните промпт источника</a>.
            } @else {
              дополните промпт источника.
            }
          </p>
        }

        @if (c.sourceId) {
          <div class="field">
            <label>Статус источника (необязательно)</label>
            <div class="tiles" role="group" aria-label="Статус источника">
              @for (status of statuses(); track status.id) {
                <button type="button" class="tile" [class]="'tile tone-' + status.color"
                  [class.is-proposed]="status.id === proposedStatusId()"
                  [attr.aria-pressed]="statusId() === status.id" (click)="pickStatus(status.id)">
                  <app-icon [name]="iconName(status.icon)" />
                  <span class="grow">{{ status.name }}</span>
                  @if (status.id === proposedStatusId()) { <small>предложено</small> }
                </button>
              } @empty {
                <small>{{ statusesLoading() ? 'Загрузка статусов…' : 'У источника нет статусов.' }}</small>
              }
            </div>
            <span class="hint">Оставьте пустым, чтобы принять предложенный системой статус.</span>
          </div>
        }

        <app-form-error [error]="formError()" />

        <div class="row">
          <button type="button" class="btn btn--primary approve" [disabled]="!canApprove() || submitting()"
            (click)="approve()">
            <app-icon name="check" />{{ submitting() ? 'Отправка…' : 'Принять' }}
          </button>
          <button type="button" class="btn reject" [disabled]="submitting()" (click)="reject()">
            <app-icon name="cross" />Отклонить
          </button>
        </div>

        <small class="muted">Кейс «{{ kindLabel[c.kind] }}». Решение попадёт в журнал аудита.</small>
      </section>
    }`,
  styles: `
    .resolve { padding-top: 4px; border-top: 1px solid var(--line-soft); }
    .tiles { display: grid; gap: 8px; grid-template-columns: repeat(auto-fill, minmax(150px, 1fr)); }
    .tile { display: flex; align-items: center; gap: 8px; min-height: 48px; padding: 8px 10px; text-align: left; cursor: pointer;
      border: 2px solid color-mix(in srgb, var(--tone, var(--st-stale)) 45%, transparent);
      border-radius: var(--r-md); background: color-mix(in srgb, var(--tone, var(--st-stale)) 10%, var(--surface));
      color: var(--tone, var(--ink)); font-weight: 600; }
    .tile[aria-pressed='true'] { border-color: var(--tone); background: color-mix(in srgb, var(--tone) 22%, var(--surface));
      box-shadow: var(--focus-ring); }
    .tile small { color: var(--ink-3); font-weight: 500; }
    .tile.is-proposed::before { content: '★'; color: var(--brand); }
  `,
})
export class ResolveModerationCase {
  private readonly api = inject(ModerationApi);
  private readonly sources = inject(SourcesApi);
  private readonly catalog = inject(CatalogObjectsApi);
  private readonly confirm = inject(ConfirmService);
  private readonly toasts = inject(ToastService);

  readonly item = input.required<ModerationCase>();
  readonly resolved = output<ModerationCase>();

  protected readonly kindLabel = CASE_KIND_LABEL;
  protected readonly iconName = normalizeIcon;
  protected readonly mapObjectValue = signal<string[]>([]);
  protected readonly statusId = signal('');
  protected readonly statuses = signal<SourceStatus[]>([]);
  protected readonly statusesLoading = signal(true);
  protected readonly submitting = signal(false);
  protected readonly formError = signal<ApiError | null>(null);

  protected readonly needsObject = computed(() => this.item().kind === 'object_not_found');
  protected readonly proposedStatusId = computed(
    () => this.item().proposed?.sourceStatusId ?? '',
  );
  protected readonly canApprove = computed(
    () => !this.needsObject() || this.mapObjectValue().length > 0,
  );

  protected readonly objectLoader: PickerLoader = async (q, cursor) => {
    const page = await this.catalog.list({
      sourceId: this.item().sourceId ?? undefined,
      q: q || undefined,
      cursor: cursor ?? undefined,
      limit: PICKER_LIMIT,
    });
    return {
      items: page.items.map((object) => ({
        value: object.id,
        label: object.name,
        hint: object.address ?? undefined,
      })),
      nextCursor: page.nextCursor,
    };
  };

  private lastCaseId = '';

  constructor() {
    effect(() => {
      const item = this.item();
      if (item.id !== this.lastCaseId) {
        this.lastCaseId = item.id;
        untracked(() => {
          this.mapObjectValue.set([]);
          this.statusId.set('');
          this.formError.set(null);
        });
      }
      const sourceId = item.sourceId;
      untracked(() => void this.loadStatuses(sourceId));
    });
  }

  protected pickStatus(id: string): void {
    this.statusId.update((current) => (current === id ? '' : id));
  }

  protected async approve(): Promise<void> {
    if (!this.canApprove() || this.submitting()) return;

    const body: ApproveCaseRequest = {};
    if (this.needsObject()) {
      const mapObjectId = this.mapObjectValue()[0];
      if (!mapObjectId) return;
      body.mapObjectId = mapObjectId;
    }
    const sourceStatusId = this.statusId();
    if (sourceStatusId) body.sourceStatusId = sourceStatusId;

    await this.send(() => this.api.approve(this.item().id, body), 'Кейс принят');
  }

  protected async reject(): Promise<void> {
    if (this.submitting()) return;

    const answer = await this.confirm.ask({
      title: 'Отклонить кейс?',
      text: 'Кейс закроется без изменения статуса фиксации.',
      confirmLabel: 'Отклонить',
      danger: true,
      reasonLabel: 'Причина (необязательно)',
    });
    if (!answer.confirmed) return;

    const body: RejectCaseRequest = answer.reason ? { reason: answer.reason } : {};
    await this.send(() => this.api.reject(this.item().id, body), 'Кейс отклонён');
  }

  private async send(action: () => Promise<ModerationCase>, success: string): Promise<void> {
    this.submitting.set(true);
    this.formError.set(null);
    try {
      const updated = await action();
      this.toasts.success(success);
      this.resolved.emit(updated);
    } catch (err) {
      const error = toApiError(err);
      if (error.status === 422) this.formError.set(error);
      else this.toasts.fromError(error);
    } finally {
      this.submitting.set(false);
    }
  }

  private async loadStatuses(sourceId: string | null | undefined): Promise<void> {
    if (!sourceId) {
      this.statuses.set([]);
      this.statusesLoading.set(false);
      return;
    }
    this.statusesLoading.set(true);
    this.statuses.set([]);
    try {
      const source = await this.sources.get(sourceId);
      this.statuses.set(source.statuses);
    } catch {
      this.statuses.set([]);
    } finally {
      this.statusesLoading.set(false);
    }
  }
}
