import { Component, computed, inject, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ObservationsApi } from '@entities/observation';
import { SessionService } from '@entities/user';
import { errorText, toApiError } from '@shared/api/api-error';
import type {
  CreateObservationRequest,
  CreateObservationResponse,
  MapObjectCard,
  SourceCardView,
} from '@shared/api/types';
import { P } from '@shared/lib/permissions';
import { Icon, normalizeIcon } from '@shared/ui/icon';
import { ToastService } from '@shared/ui/toast';

const COMMENT_MAX = 500;

/** Отметка статуса по источнику фиксации. */
@Component({
  selector: 'app-report-status',
  imports: [RouterLink, Icon],
  template: `
    @if (!allowed()) {
      <p class="callout callout--info">
        <app-icon name="lock" />
        <span>Чтобы отметить статус, <a routerLink="/login">войдите</a>. Вход через MAX будет доступен позже.</span>
      </p>
    } @else if (card().sources.length === 0) {
      <p class="callout"><app-icon name="alert" /><span>У фиксации нет источников, поэтому отметить статус нельзя.</span></p>
    } @else {
      <form class="stack" (submit)="submit($event)">
        <h3>Отметить статус</h3>

        <div class="field">
          <label for="rs-source">Источник</label>
          <select id="rs-source" [value]="sourceId()" (change)="pickSource($any($event.target).value)">
            @for (source of card().sources; track source.id) { <option [value]="source.id">{{ source.name }}</option> }
          </select>
        </div>

        <div class="field">
          <label>Статус</label>
          <div class="tiles" role="group" [attr.aria-label]="'Статус источника ' + (source()?.name ?? '')">
            @for (status of statuses(); track status.id) {
              <button type="button" class="tile" [class]="'tile tone-' + status.color"
                [attr.aria-pressed]="statusId() === status.id" (click)="statusId.set(status.id)">
                <app-icon [name]="iconName(status.icon)" />{{ status.name }}
              </button>
            } @empty {
              <small>У источника нет статусов.</small>
            }
          </div>
          @if (fieldErrors()['sourceStatusId']; as message) { <span class="err">{{ message }}</span> }
        </div>

        <div class="field">
          <label for="rs-comment">Комментарий (необязательно)</label>
          <textarea id="rs-comment" [value]="comment()" [attr.maxlength]="max" (input)="comment.set($any($event.target).value)"
            placeholder="Что видно на месте"></textarea>
          <span class="hint">{{ comment().length }}/{{ max }}</span>
          @if (fieldErrors()['comment']; as message) { <span class="err">{{ message }}</span> }
        </div>

        <label class="check">
          <input type="checkbox" [checked]="shareLocation()" (change)="shareLocation.set($any($event.target).checked)" />
          <span>Передать моё местоположение (только для расчёта расстояния, координаты не сохраняются)</span>
        </label>

        @if (formError(); as message) {
          <p class="callout"><app-icon name="alert" /><span>{{ message }}</span></p>
        }

        <div class="row">
          <button class="btn btn--primary" type="submit" [disabled]="!statusId() || submitting()">
            {{ submitting() ? 'Отправка…' : 'Отправить' }}
          </button>
        </div>
      </form>
    }`,
  styles: `
    .tiles { display: grid; gap: 8px; grid-template-columns: repeat(auto-fill, minmax(140px, 1fr)); }
    .tile { display: flex; align-items: center; gap: 8px; min-height: 52px; padding: 10px 12px; text-align: left; cursor: pointer;
      border: 2px solid color-mix(in srgb, var(--tone, var(--st-stale)) 45%, transparent);
      border-radius: var(--r-md); background: color-mix(in srgb, var(--tone, var(--st-stale)) 10%, var(--surface));
      color: var(--tone, var(--ink)); font-weight: 600; }
    .tile[aria-pressed='true'] { border-color: var(--tone); background: color-mix(in srgb, var(--tone) 22%, var(--surface)); box-shadow: var(--focus-ring); }
    .check { display: flex; align-items: flex-start; gap: 8px; font-size: 14px; cursor: pointer; }
    .check input { flex: none; width: 16px; height: 16px; margin-top: 3px; }
    .check span { flex: 1 1 auto; min-width: 0; }
    .err { color: var(--st-none); font-size: 12px; }
  `,
})
export class ReportStatus {
  private readonly session = inject(SessionService);
  private readonly observations = inject(ObservationsApi);
  private readonly toasts = inject(ToastService);

  readonly card = input.required<MapObjectCard>();
  readonly reported = output<CreateObservationResponse>();

  protected readonly max = COMMENT_MAX;
  protected readonly iconName = normalizeIcon;
  protected readonly allowed = computed(() => this.session.has(P.observationCreate));

  protected readonly sourceId = signal('');
  protected readonly statusId = signal('');
  protected readonly comment = signal('');
  protected readonly shareLocation = signal(false);
  protected readonly submitting = signal(false);
  protected readonly fieldErrors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  protected readonly source = computed<SourceCardView | undefined>(() => {
    const sources = this.card().sources;
    return sources.find((s) => s.id === this.sourceId()) ?? sources[0];
  });
  protected readonly statuses = computed(() => this.source()?.statuses ?? []);

  protected pickSource(id: string): void {
    this.sourceId.set(id);
    this.statusId.set('');
  }

  protected async submit(event: Event): Promise<void> {
    event.preventDefault();
    const source = this.source();
    if (!source || !this.statusId() || this.submitting()) return;

    this.submitting.set(true);
    this.fieldErrors.set({});
    this.formError.set(null);
    try {
      const location = this.shareLocation() ? await this.locate() : null;
      const body: CreateObservationRequest = {
        mapObjectId: this.card().id,
        sourceId: source.id,
        sourceStatusId: this.statusId(),
        ...(this.comment().trim() ? { comment: this.comment().trim() } : {}),
        ...(location ? { location } : {}),
      };
      const response = await this.observations.create(body);
      this.toasts.success('Отметка принята');
      this.statusId.set('');
      this.comment.set('');
      this.shareLocation.set(false);
      this.reported.emit(response);
    } catch (err) {
      const error = toApiError(err);
      if (error.status === 422) {
        const fields = error.fieldErrors;
        this.fieldErrors.set(fields);
        const shown = ['sourceStatusId', 'comment'].some((field) => Boolean(fields[field]));
        this.formError.set(shown ? null : errorText(error));
      } else if (error.status === 429) {
        this.toasts.error('Слишком много отметок, повторите позже');
      } else {
        this.toasts.fromError(error);
      }
    } finally {
      this.submitting.set(false);
    }
  }

  /** Геолокация — мягкий сигнал: отказ или таймаут не мешают отправке. */
  private locate(): Promise<{ lat: number; lon: number } | null> {
    return new Promise((resolve) => {
      if (typeof navigator === 'undefined' || !navigator.geolocation) {
        resolve(null);
        return;
      }
      const timer = setTimeout(() => resolve(null), 5000);
      navigator.geolocation.getCurrentPosition(
        (position) => {
          clearTimeout(timer);
          resolve({ lat: position.coords.latitude, lon: position.coords.longitude });
        },
        () => {
          clearTimeout(timer);
          resolve(null);
        },
        { timeout: 5000, maximumAge: 60000 },
      );
    });
  }
}
