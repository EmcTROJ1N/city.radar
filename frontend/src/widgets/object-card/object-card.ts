import {
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
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
import { TabItem, Tabs } from '@shared/ui/tabs';
import { ToastService } from '@shared/ui/toast';

const OBSERVATION_LIMIT = 20;

/** Окно сводки по зоне — то же, что у бэкенда (StatusResolver.ZoneWindow). */
const ZONE_WINDOW_MS = 60 * 60 * 1000;

type DetailsTab = 'observations' | 'sources';

// В jsdom нет showModal/close — открываем атрибутом (как в shared/ui/confirm.ts).
function openDialog(dialog: HTMLDialogElement): void {
  if (dialog.open) return;
  if (typeof dialog.showModal === 'function') dialog.showModal();
  else dialog.setAttribute('open', '');
}

function closeDialog(dialog: HTMLDialogElement): void {
  if (!dialog.open) return;
  if (typeof dialog.close === 'function') dialog.close();
  else dialog.removeAttribute('open');
}

interface ZoneSummaryItem {
  readonly status: PublicObservation['status'];
  readonly count: number;
}

/**
 * Карточка фиксации: в панели — статус, сводка и последняя отметка; сводка, источники
 * и лента отметок — в окне «Подробнее», форма отметки — в окне «Отметить статус».
 */
@Component({
  selector: 'app-object-card',
  imports: [Icon, StatusBadge, Tabs, ReportStatus, ComplainStatus, RetractObservationButton],
  template: `
    <article class="card stack passport" [class]="'card stack passport tone-' + (card()?.status?.color ?? 'stale')">
      <header class="row head">
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
        <dl class="kv facts">
          <dt>Статус</dt>
          <dd>
            @if (c.status; as status) {
              <app-status-badge [label]="status.name" [tone]="status.color" [icon]="icon(status.icon)"
                [stale]="status.freshness !== 'fresh'" />
            } @else {
              <span class="muted">Неизвестно, нет актуальных данных</span>
            }
          </dd>
          @if (c.status; as status) {
            <dt>Обновлено</dt>
            <dd>{{ ago(status.observedAt) }} · {{ observationLabel[status.sourceType] }}</dd>
            <dt>Наблюдений</dt>
            <dd>{{ status.observationCount }} · {{ freshnessLabel[status.freshness] }}</dd>
          }
          @if (zoneSummary(); as summary) {
            <dt>За час</dt>
            <dd>
              @for (item of summary; track item.status.code) {
                <app-status-badge [label]="item.status.name + ' · ' + item.count" [tone]="item.status.color"
                  [icon]="icon(item.status.icon)" />
              }
            </dd>
          }
        </dl>

        <dl class="kv facts latest">
          @if (latestObservation(); as last) {
            <dt>Последнее</dt>
            <dd>
              <app-status-badge [label]="last.status.name" [tone]="last.status.color" [icon]="icon(last.status.icon)"
                [stale]="expired(last)" />
              <span class="muted">{{ ago(last.observedAt) }}</span>
            </dd>
            @if (last.address) {
              <dt>Адрес</dt>
              <dd>{{ last.address }}</dd>
            }
            @if (last.text) {
              <dt>Сообщение</dt>
              <dd><span class="clamp">{{ last.text }}</span></dd>
            }
          } @else {
            <dt>Последнее</dt>
            <dd class="muted">Пока нет отметок.</dd>
          }
        </dl>

        <div class="card-actions">
          <button class="btn btn--primary" type="button" (click)="openDetails('observations')">
            <app-icon name="info" />Подробнее
          </button>
          <button class="btn" type="button" (click)="openReport()">
            <app-icon name="drop" />Отметить статус
          </button>
        </div>
        <app-complain-status [objectId]="c.id" />
      }
    </article>

    <dialog #details class="modal details" aria-labelledby="details-title" (cancel)="onCancel($event, 'details')"
      (close)="closeDetails()" (click)="onBackdrop($event, 'details')">
      @if (detailsTab() && card(); as c) {
        <div class="modal__box stack">
          <header class="row head">
            <div class="grow stack">
              <h2 id="details-title">{{ c.name }}</h2>
              @if (c.status; as status) {
                <div class="row">
                  <app-status-badge [label]="status.name" [tone]="status.color" [icon]="icon(status.icon)"
                    [stale]="status.freshness !== 'fresh'" />
                  <small>обновлено {{ ago(status.observedAt) }}</small>
                </div>
              }
              @if (isZone()) {
                <small class="muted">Статус зоны — сводка отметок за час: у всех нет — «нет», почти у всех есть — «есть»,
                  иначе — «частично».</small>
              }
            </div>
            <button class="btn btn--ghost btn--icon" type="button" aria-label="Закрыть окно" (click)="closeDetails()">
              <app-icon name="cross" />
            </button>
          </header>

          <app-tabs [tabs]="detailsTabs" [active]="detailsTab() ?? 'observations'"
            (activeChange)="openDetails($any($event))" />

          @switch (detailsTab()) {
            @case ('sources') {
              <div class="table-wrap table-wrap--cards">
                <table class="table table--cards sources">
                  <thead>
                    <tr><th>Источник</th><th>Тип</th><th>Таймаут</th><th>Последняя отметка</th><th>Когда</th><th>Достоверность</th></tr>
                  </thead>
                  <tbody>
                    @for (source of c.sources; track source.id) {
                      <tr>
                        <td class="cell-main" data-label="Источник"><strong>{{ source.name }}</strong></td>
                        <td class="nowrap" data-label="Тип">
                          <span class="type"><app-icon [name]="typeIcon(source.sourceType)" />{{ typeLabel[source.sourceType] }}</span>
                        </td>
                        <td class="nowrap" data-label="Таймаут">{{ duration(source.timeoutSeconds) }}</td>
                        @if (source.lastObservation; as last) {
                          <td data-label="Последняя отметка">
                            <app-status-badge [label]="last.status.name" [tone]="last.status.color" [icon]="icon(last.status.icon)"
                              [stale]="expired(last)" />
                          </td>
                          <td class="nowrap" data-label="Когда">{{ ago(last.observedAt) }}</td>
                          <td class="nowrap" data-label="Достоверность">{{ last.confidence != null ? percent(last.confidence) : '—' }}</td>
                        } @else {
                          <td data-label="Последняя отметка" class="muted">нет актуальных данных</td>
                          <td data-label="Когда">—</td>
                          <td data-label="Достоверность">—</td>
                        }
                      </tr>
                    } @empty {
                      <tr><td colspan="6" class="empty">Источники не привязаны.</td></tr>
                    }
                  </tbody>
                </table>
              </div>
            }
            @case ('observations') {
              <div class="table-wrap table-wrap--cards">
                <table class="table table--cards obs">
                  <thead>
                    <tr>
                      <th>Когда</th><th>Статус</th><th>Сообщение</th><th>Источник</th>
                      @if (canRetract()) { <th><span class="visually-hidden">Действия</span></th> }
                    </tr>
                  </thead>
                  <tbody>
                    @for (observation of observations(); track observation.id) {
                      <tr>
                        <td class="nowrap" data-label="Когда">{{ ago(observation.observedAt) }}</td>
                        <td class="nowrap" data-label="Статус">
                          <app-status-badge [label]="observation.status.name" [tone]="observation.status.color"
                            [icon]="icon(observation.status.icon)" [stale]="expired(observation)" />
                        </td>
                        <td class="cell-main text" data-label="Сообщение">
                          @if (observation.address) {
                            <span class="address"><app-icon name="pin" />{{ observation.address }}</span>
                          }
                          @if (observation.text) { <span class="msg">{{ observation.text }}</span> }
                          @if (observation.geoDistanceM != null) {
                            <small class="muted dist">≈ {{ distance(observation.geoDistanceM!) }} от фиксации</small>
                          }
                        </td>
                        <td class="src" data-label="Источник">
                          <span class="muted">{{ observationLabel[observation.sourceType] }}</span>
                          @if (observation.sourceName) { <small class="src-name">{{ observation.sourceName }}</small> }
                        </td>
                        @if (canRetract()) {
                          <td class="actions">
                            <app-retract-observation-button [iconOnly]="true" [observationId]="observation.id"
                              (retracted)="onRetracted($event)" />
                          </td>
                        }
                      </tr>
                    } @empty {
                      <tr><td [attr.colspan]="canRetract() ? 5 : 4" class="empty">Пока нет отметок.</td></tr>
                    }
                  </tbody>
                </table>
              </div>
              @if (nextCursor()) {
                <button class="btn btn--ghost btn--sm more" type="button" [disabled]="moreLoading()" (click)="loadMore()">
                  {{ moreLoading() ? 'Загрузка…' : 'Показать ещё' }}
                </button>
              }
            }
          }
        </div>
      }
    </dialog>

    <dialog #report class="modal modal--narrow report" aria-labelledby="report-title" (cancel)="onCancel($event, 'report')"
      (close)="closeReport()" (click)="onBackdrop($event, 'report')">
      @if (reportOpen() && card(); as c) {
        <div class="modal__box stack">
          <header class="row head">
            <div class="grow">
              <h2 id="report-title">Отметить статус</h2>
              <small>{{ c.name }}</small>
            </div>
            <button class="btn btn--ghost btn--icon" type="button" aria-label="Закрыть окно" (click)="closeReport()">
              <app-icon name="cross" />
            </button>
          </header>
          <app-report-status [card]="c" (reported)="onReported($event)" />
        </div>
      }
    </dialog>`,
  styles: `
    :host { display: block; min-width: 0; }
    .passport { border-top: 6px solid var(--tone, var(--line)); min-width: 0; overflow-wrap: anywhere; }
    // Карточка живёт в узкой правой колонке: длинные имена, адреса и ссылки
    // переносим, иначе текст вылезает за фон и появляется горизонтальный скролл.
    .passport > *, .passport .row > *, .kv > * { min-width: 0; }
    h2 { font-size: 22px; overflow-wrap: anywhere; }
    .head { align-items: flex-start; }
    // Одна сетка «подпись — значение» на всю панель: подписи одной ширины, значения в одну колонку.
    // Строка — высотой с бейдж: подпись и значение стоят на одной линии, даже если значение переносится.
    .facts { grid-template-columns: 104px minmax(0, 1fr); align-items: start; gap: 8px 14px; margin: 0; }
    .facts dt { font-size: 13px; line-height: 28px; }
    .facts dd { min-height: 28px; display: flex; flex-wrap: wrap; align-items: center; gap: 6px; line-height: 1.4; }
    .latest { padding-top: 12px; border-top: 1px solid var(--line-soft); }
    .clamp { display: -webkit-box; -webkit-line-clamp: 3; -webkit-box-orient: vertical; overflow: hidden; color: var(--ink-2); }
    .card-actions { display: grid; grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); gap: 8px;
      padding-top: 12px; border-top: 1px solid var(--line-soft); }
    .card-actions .btn { justify-content: center; white-space: nowrap; }
    // Окна поверх карты: широкое — разделы, узкое — отметка статуса; на телефоне — во весь экран.
    .modal { width: min(960px, calc(100vw - 32px)); max-width: none; max-height: 90vh; padding: 0; border: 0;
      background: transparent; overflow: visible; }
    .modal--narrow { width: min(560px, calc(100vw - 32px)); }
    .modal::backdrop { background: rgb(15 42 51 / 0.45); }
    .modal__box { max-height: 90vh; overflow: auto; padding: var(--pad); background: var(--surface);
      border: 1px solid var(--line); border-radius: var(--r-lg); box-shadow: var(--shadow-float);
      min-width: 0; align-content: start; }
    .modal__box > *, .modal__box .row > * { min-width: 0; }
    // Высота окна разделов не прыгает при переключении вкладок.
    .details .modal__box { min-height: min(560px, 90vh); }
    // Таблицы окна: короткие колонки не переносятся, длинный текст переносится по словам.
    .table td { vertical-align: top; overflow-wrap: break-word; }
    .nowrap { white-space: nowrap; }
    .obs .text { min-width: 260px; width: 100%; color: var(--ink); }
    .obs .address { display: flex; align-items: center; gap: 4px; margin-bottom: 2px; color: var(--ink-2); font-size: 13px; font-weight: 600; }
    .obs .msg { display: block; }
    .obs .src { min-width: 140px; }
    .type { display: inline-flex; align-items: center; gap: 6px; }
    .dist, .src-name { display: block; }
    .actions app-retract-observation-button { display: inline-flex; }
    .more { justify-self: center; }
    @media (max-width: 640px) {
      .modal, .modal--narrow { width: 100vw; height: 100dvh; max-height: none; margin: 0; }
      .modal__box { height: 100%; max-height: none; border-radius: 0; border: 0; }
      .obs .text { min-width: 0; }
    }
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

  protected readonly isZone = computed(() => this.card()?.coverageKind === 'polygon');

  /** Открытый раздел окна подробностей; <c>null</c> — окно закрыто. */
  protected readonly detailsTab = signal<DetailsTab | null>(null);

  /** Сводка по статусам уже есть в панели, поэтому в окне — только лента отметок и источники. */
  protected readonly detailsTabs: (TabItem & { id: DetailsTab })[] = [
    { id: 'observations', label: 'Отметки' },
    { id: 'sources', label: 'Источники' },
  ];

  /** Самая свежая отметка — превью в панели. Лента приходит от свежих к старым. */
  protected readonly latestObservation = computed<PublicObservation | null>(() => this.observations()[0] ?? null);

  private readonly details = viewChild<ElementRef<HTMLDialogElement>>('details');

  /** Окно «Отметить статус»: форма со всеми статусами источника. */
  protected readonly reportOpen = signal(false);

  private readonly report = viewChild<ElementRef<HTMLDialogElement>>('report');

  /** Сводка по статусам актуальных отметок зоны за час до самой свежей — как считает бэкенд. */
  protected readonly zoneSummary = computed<ZoneSummaryItem[] | null>(() => {
    if (!this.isZone()) return null;
    const actual = this.observations().filter((observation) => !this.expired(observation));
    if (actual.length === 0) return null;

    const latest = Math.max(...actual.map((observation) => Date.parse(observation.observedAt)));
    const counts = new Map<string, ZoneSummaryItem>();
    for (const observation of actual) {
      if (Date.parse(observation.observedAt) <= latest - ZONE_WINDOW_MS) continue;
      const item = counts.get(observation.status.code);
      counts.set(observation.status.code, { status: observation.status, count: (item?.count ?? 0) + 1 });
    }
    return [...counts.values()].sort((a, b) => b.count - a.count);
  });

  private seq = 0;

  constructor() {
    effect(() => {
      const id = this.objectId();
      this.refreshKey();
      if (!id) return;
      untracked(() => void this.load(id));
    });

    effect(() => {
      const dialog = this.details()?.nativeElement;
      if (!dialog) return;
      if (this.detailsTab()) openDialog(dialog);
      else closeDialog(dialog);
    });

    effect(() => {
      const dialog = this.report()?.nativeElement;
      if (!dialog) return;
      if (this.reportOpen()) openDialog(dialog);
      else closeDialog(dialog);
    });
  }

  protected openDetails(tab: DetailsTab): void {
    this.detailsTab.set(tab);
  }

  protected closeDetails(): void {
    this.detailsTab.set(null);
  }

  protected openReport(): void {
    this.reportOpen.set(true);
  }

  protected closeReport(): void {
    this.reportOpen.set(false);
  }

  protected onCancel(event: Event, which: 'details' | 'report'): void {
    event.preventDefault();
    this.closeModal(which);
  }

  /** Клик по фону вокруг окна: цель — сам dialog, а не его содержимое. */
  protected onBackdrop(event: MouseEvent, which: 'details' | 'report'): void {
    if (event.target === event.currentTarget) this.closeModal(which);
  }

  private closeModal(which: 'details' | 'report'): void {
    if (which === 'details') this.closeDetails();
    else this.closeReport();
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
    this.closeReport();
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
      this.detailsTab.set(null);
      this.reportOpen.set(false);
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
