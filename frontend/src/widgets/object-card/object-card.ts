import { Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { MapApi } from '@entities/map-object';
import { SessionService } from '@entities/user';
import { ComplainStatus } from '@features/complain-status/complain-status';
import { ReportStatus } from '@features/report-status/report-status';
import { RetractObservationButton } from '@features/retract-observation/retract-observation-button';
import { errorText, toApiError } from '@shared/api/api-error';
import type {
  CreateObservationResponse,
  MapObjectCard,
  PublicObservation,
  RetractResult,
  SourceType,
} from '@shared/api/types';
import { agoIso, durationSec } from '@shared/lib/format';
import { FRESHNESS_LABEL, OBSERVATION_SOURCE_LABEL, SOURCE_TYPE_LABEL } from '@shared/lib/labels';
import { P } from '@shared/lib/permissions';
import { Icon, IconName, normalizeIcon } from '@shared/ui/icon';
import { StatusBadge } from '@shared/ui/status-badge';
import { ToastService } from '@shared/ui/toast';

const OBSERVATION_LIMIT = 20;

/** Карточка фиксации: итоговый статус, источники, публичные отметки. */
@Component({
  selector: 'app-object-card',
  imports: [Icon, StatusBadge, ReportStatus, ComplainStatus, RetractObservationButton],
  template: `
    <article class="card stack passport" [class]="'card stack passport tone-' + (card()?.status?.color ?? 'stale')">
      <header class="row">
        <div class="grow">
          @if (card(); as c) {
            <h2>{{ c.name }}</h2>
            @if (c.address) { <small>{{ c.address }}</small> }
          } @else {
            <h2>Фиксация</h2>
          }
        </div>
        <button class="btn btn--ghost btn--icon" type="button" aria-label="Закрыть карточку" (click)="close.emit()">
          <app-icon name="cross" />
        </button>
      </header>

      @if (loading() && !card()) {
        <p class="muted">Загрузка…</p>
      } @else if (notFound()) {
        <p class="callout"><app-icon name="alert" /><span>Фиксация не найдена.</span></p>
      } @else if (error(); as message) {
        <p class="callout"><app-icon name="alert" /><span>{{ message }}</span></p>
      } @else if (card(); as c) {
        @if (c.status; as status) {
          <div class="stack">
            <div class="row">
              <app-status-badge [label]="status.name" [tone]="status.color" [icon]="icon(status.icon)"
                [stale]="status.freshness !== 'fresh'" />
              <small>обновлено {{ ago(status.observedAt) }}</small>
            </div>
            <small>{{ freshnessLabel[status.freshness] }} · наблюдений: {{ status.observationCount }} ·
              источник: {{ observationLabel[status.sourceType] }}</small>
          </div>
        } @else {
          <p class="callout"><app-icon name="alert" /><span>Неизвестно, нет актуальных данных.</span></p>
        }

        <section>
          <h3>Источники</h3>
          @for (source of c.sources; track source.id) {
            <div class="src stack">
              <div class="row">
                <app-icon [name]="typeIcon(source.sourceType)" />
                <span class="chip">{{ typeLabel[source.sourceType] }}</span>
                <strong class="grow">{{ source.name }}</strong>
                <span class="chip">таймаут {{ duration(source.timeoutSeconds) }}</span>
              </div>
              @if (source.lastObservation; as last) {
                <div class="row">
                  <app-status-badge [label]="last.status.name" [tone]="last.status.color" [icon]="icon(last.status.icon)"
                    [stale]="expired(last)" />
                  <small>{{ ago(last.observedAt) }}</small>
                  @if (last.confidence != null) {
                    <span class="chip">достоверность {{ percent(last.confidence) }}</span>
                  }
                </div>
              } @else {
                <small>нет актуальных данных</small>
              }
            </div>
          } @empty {
            <p class="muted">Источники не привязаны.</p>
          }
        </section>

        <section>
          <h3>Отметки</h3>
          <ul class="obs">
            @for (observation of observations(); track observation.id) {
              <li>
                <div class="row">
                  <app-status-badge [label]="observation.status.name" [tone]="observation.status.color"
                    [icon]="icon(observation.status.icon)" [stale]="expired(observation)" />
                  <span class="chip">{{ observationLabel[observation.sourceType] }}</span>
                  @if (observation.sourceName) { <small class="grow">{{ observation.sourceName }}</small> }
                  <small>{{ ago(observation.observedAt) }}</small>
                  @if (canRetract()) {
                    <app-retract-observation-button [iconOnly]="true" [observationId]="observation.id"
                      (retracted)="onRetracted($event)" />
                  }
                </div>
                @if (observation.text) { <blockquote>{{ observation.text }}</blockquote> }
                @if (observation.geoDistanceM != null) {
                  <small>≈ {{ distance(observation.geoDistanceM!) }} от фиксации</small>
                }
              </li>
            } @empty {
              <li class="muted">Пока нет отметок.</li>
            }
          </ul>
          @if (nextCursor()) {
            <button class="btn btn--ghost btn--sm" type="button" [disabled]="moreLoading()" (click)="loadMore()">
              {{ moreLoading() ? 'Загрузка…' : 'Показать ещё' }}
            </button>
          }
        </section>

        <app-report-status [card]="c" (reported)="onReported($event)" />
        <app-complain-status [objectId]="c.id" />

        <footer class="stack">
          <button class="btn btn--ghost btn--sm about" type="button" (click)="about.emit()">
            <app-icon name="info" />О достоверности данных
          </button>
          <small>Данные поступают из чатов и от жителей, достоверность не гарантируется. Побеждает самое свежее актуальное наблюдение.</small>
        </footer>
      }
    </article>`,
  styles: `
    :host { display: block; }
    .passport { border-top: 6px solid var(--tone, var(--line)); }
    h2 { font-size: 22px; }
    h3 { margin-bottom: 8px; }
    .src { padding: 8px 0; border-bottom: 1px solid var(--line-soft); }
    .src:last-child { border-bottom: 0; }
    .obs { list-style: none; margin: 0; padding: 0; display: grid; gap: 10px; max-height: 260px; overflow: auto; }
    blockquote { margin: 4px 0 0; padding-left: 10px; border-left: 3px solid var(--line); color: var(--ink-2); font-size: 14px; }
    .about { justify-self: start; padding-left: 0; }
  `,
})
export class ObjectCard {
  private readonly api = inject(MapApi);
  private readonly session = inject(SessionService);
  private readonly toasts = inject(ToastService);

  readonly objectId = input.required<string>();
  readonly refreshKey = input(0);

  readonly close = output<void>();
  readonly reported = output<CreateObservationResponse>();
  readonly retracted = output<RetractResult>();
  readonly about = output<void>();

  protected readonly freshnessLabel = FRESHNESS_LABEL;
  protected readonly observationLabel = OBSERVATION_SOURCE_LABEL;
  protected readonly typeLabel = SOURCE_TYPE_LABEL;
  protected readonly ago = agoIso;
  protected readonly duration = durationSec;
  protected readonly icon = normalizeIcon;
  protected readonly canRetract = computed(() => this.session.has(P.observationRetract));

  protected readonly card = signal<MapObjectCard | null>(null);
  protected readonly observations = signal<PublicObservation[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly moreLoading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly notFound = signal(false);

  private seq = 0;

  constructor() {
    effect(() => {
      const id = this.objectId();
      this.refreshKey();
      if (!id) return;
      untracked(() => void this.load(id));
    });
  }

  protected typeIcon(type: SourceType): IconName {
    if (type === 'site') return 'external';
    return type === 'max_chat' ? 'message' : 'send';
  }

  protected expired(observation: PublicObservation): boolean {
    const expires = Date.parse(observation.expiresAt);
    return Number.isFinite(expires) && expires < Date.now();
  }

  protected percent(value: number): string {
    return `${Math.round(value * 100)}%`;
  }

  protected distance(meters: number): string {
    return meters >= 1000 ? `${(meters / 1000).toFixed(1)} км` : `${Math.round(meters)} м`;
  }

  protected async loadMore(): Promise<void> {
    const cursor = this.nextCursor();
    const id = this.objectId();
    if (!cursor || !id || this.moreLoading()) return;

    this.moreLoading.set(true);
    try {
      const page = await this.api.listObservations(id, OBSERVATION_LIMIT, cursor);
      this.observations.update((list) => [...list, ...page.items]);
      this.nextCursor.set(page.nextCursor);
    } catch (err) {
      this.toasts.fromError(err);
    } finally {
      this.moreLoading.set(false);
    }
  }

  protected onReported(response: CreateObservationResponse): void {
    this.card.update((card) => (card ? { ...card, status: response.status } : card));
    this.reported.emit(response);
  }

  protected onRetracted(result: RetractResult): void {
    this.card.update((card) => (card ? { ...card, status: result.status } : card));
    this.retracted.emit(result);
    const id = this.objectId();
    void this.load(id).then(() => {
      // Проекция карточки обновляется с задержкой: пересчитанный статус не затираем.
      if (this.objectId() === id) {
        this.card.update((card) => (card ? { ...card, status: result.status } : card));
      }
    });
  }

  private async load(id: string): Promise<void> {
    const seq = ++this.seq;
    if (this.card()?.id !== id) {
      this.card.set(null);
      this.observations.set([]);
      this.nextCursor.set(null);
    }
    this.loading.set(true);
    this.error.set(null);
    this.notFound.set(false);
    try {
      const [card, page] = await Promise.all([
        this.api.getCard(id),
        this.api.listObservations(id, OBSERVATION_LIMIT),
      ]);
      if (seq !== this.seq) return;
      this.card.set(card);
      this.observations.set(page.items);
      this.nextCursor.set(page.nextCursor);
    } catch (err) {
      if (seq !== this.seq) return;
      const error = toApiError(err);
      if (error.status === 404) this.notFound.set(true);
      else this.error.set(errorText(error));
    } finally {
      if (seq === this.seq) this.loading.set(false);
    }
  }
}
