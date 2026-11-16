import { Component, computed, inject, signal } from '@angular/core';
import { ObservationsApi } from '@entities/observation';
import { SourcesApi } from '@entities/source';
import { ApiError, toApiError } from '@shared/api/api-error';
import { SOURCE_TYPE_LABEL } from '@shared/lib/labels';
import { ConfirmService } from '@shared/ui/confirm';
import { EntityPicker, PickerLoader } from '@shared/ui/entity-picker';
import { FormError } from '@shared/ui/form-error';
import { ToastService } from '@shared/ui/toast';

const PICKER_LIMIT = 20;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

interface BulkRetractBody {
  reason: string;
  reporterId?: string;
  sourceId?: string;
  from?: string;
  to?: string;
}

/** datetime-local → ISO; пустое или некорректное значение — undefined. */
function toIso(value: string): string | undefined {
  if (!value) return undefined;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? undefined : date.toISOString();
}

/** Пакетный отзыв наблюдений по автору, источнику или периоду. */
@Component({
  selector: 'app-bulk-retract-form',
  imports: [EntityPicker, FormError],
  template: `
    <form class="card stack" (submit)="submit($event)">
      <div>
        <h2>Пакетный отзыв</h2>
        <p class="muted">Отзываются все наблюдения, подходящие под условия. Действие попадёт в журнал аудита.</p>
      </div>

      <div class="field">
        <label>Источник</label>
        <app-entity-picker [loader]="sourceLoader" [(value)]="sourceValue" placeholder="Любой источник" />
        <span class="hint">Нужен хотя бы один из условий: источник, автор или период.</span>
      </div>

      <div class="field">
        <label for="br-author">ID автора (необязательно)</label>
        <input id="br-author" type="text" [value]="reporterId()" placeholder="00000000-0000-0000-0000-000000000000"
          (input)="reporterId.set($any($event.target).value)" />
        @if (reporterInvalid()) { <span class="err">Укажите UUID автора</span> }
      </div>

      <div class="row">
        <div class="field grow">
          <label for="br-from">Период с</label>
          <input id="br-from" type="datetime-local" [value]="from()" (input)="from.set($any($event.target).value)" />
        </div>
        <div class="field grow">
          <label for="br-to">по</label>
          <input id="br-to" type="datetime-local" [value]="to()" (input)="to.set($any($event.target).value)" />
        </div>
      </div>
      @if (periodReversed()) { <span class="err">Начало периода позже конца</span> }
      @if (!periodComplete() && (from() || to())) { <span class="hint">Укажите оба края периода.</span> }

      <div class="field">
        <label for="br-reason">Причина</label>
        <textarea id="br-reason" [value]="reason()" placeholder="Например: ошибочные отметки из тестового чата"
          (input)="reason.set($any($event.target).value)"></textarea>
      </div>

      <app-form-error [error]="formError()" />

      <div class="row">
        <button class="btn btn--primary btn--danger submit" type="submit" [disabled]="!canSubmit() || submitting()">
          {{ submitting() ? 'Отзыв…' : 'Отозвать наблюдения' }}
        </button>
        @if (!canSubmit() && reason().trim()) {
          <small>Нужен автор, источник или оба края периода.</small>
        }
      </div>
    </form>`,
  styles: `
    .err { color: var(--st-none); font-size: 12px; }
    .submit { border-color: var(--st-none); }
  `,
})
export class BulkRetractForm {
  private readonly api = inject(ObservationsApi);
  private readonly sources = inject(SourcesApi);
  private readonly confirm = inject(ConfirmService);
  private readonly toasts = inject(ToastService);

  protected readonly sourceValue = signal<string[]>([]);
  protected readonly reporterId = signal('');
  protected readonly from = signal('');
  protected readonly to = signal('');
  protected readonly reason = signal('');
  protected readonly submitting = signal(false);
  protected readonly formError = signal<ApiError | null>(null);

  protected readonly reporterInvalid = computed(() => {
    const value = this.reporterId().trim();
    return value.length > 0 && !UUID.test(value);
  });
  protected readonly periodComplete = computed(() => Boolean(this.from()) && Boolean(this.to()));
  protected readonly periodReversed = computed(() => {
    const from = toIso(this.from());
    const to = toIso(this.to());
    return Boolean(from && to && from > to);
  });
  protected readonly canSubmit = computed(() => {
    const criteria =
      Boolean(this.reporterId().trim()) ||
      this.sourceValue().length > 0 ||
      this.periodComplete();
    return (
      Boolean(this.reason().trim()) &&
      criteria &&
      !this.reporterInvalid() &&
      !this.periodReversed()
    );
  });

  protected readonly sourceLoader: PickerLoader = async (q, cursor) => {
    const page = await this.sources.list({ q: q || undefined, cursor: cursor ?? undefined, limit: PICKER_LIMIT });
    return {
      items: page.items.map((source) => ({
        value: source.id,
        label: source.name,
        hint: SOURCE_TYPE_LABEL[source.sourceType],
      })),
      nextCursor: page.nextCursor,
    };
  };

  protected async submit(event: Event): Promise<void> {
    event.preventDefault();
    if (!this.canSubmit() || this.submitting()) return;

    const answer = await this.confirm.ask({
      title: 'Отозвать все подходящие наблюдения?',
      text: 'Отозванные наблюдения перестанут учитываться в статусах фиксаций.',
      confirmLabel: 'Отозвать',
      danger: true,
    });
    if (!answer.confirmed) return;

    const body: BulkRetractBody = { reason: this.reason().trim() };
    const reporterId = this.reporterId().trim();
    if (reporterId) body.reporterId = reporterId;
    const sourceId = this.sourceValue()[0];
    if (sourceId) body.sourceId = sourceId;
    const from = toIso(this.from());
    if (from) body.from = from;
    const to = toIso(this.to());
    if (to) body.to = to;

    this.submitting.set(true);
    this.formError.set(null);
    try {
      const result = await this.api.retractBulk(body);
      this.toasts.success(`Отозвано: ${result.retractedCount}`);
      this.sourceValue.set([]);
      this.reporterId.set('');
      this.from.set('');
      this.to.set('');
      this.reason.set('');
    } catch (err) {
      const error = toApiError(err);
      if (error.status === 422) this.formError.set(error);
      else this.toasts.fromError(error);
    } finally {
      this.submitting.set(false);
    }
  }
}
