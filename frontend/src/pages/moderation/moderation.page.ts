import {
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { ActivatedRoute, Params, Router } from '@angular/router';
import { ModerationApi } from '@entities/moderation-case';
import { SessionService } from '@entities/user';
import { BulkRetractForm } from '@features/retract-observation/bulk-retract-form';
import { errorText, toApiError } from '@shared/api/api-error';
import type {
  ModerationCase,
  ModerationCaseKind,
  ModerationCaseState,
  ModerationCaseSummary,
} from '@shared/api/types';
import { agoIso } from '@shared/lib/format';
import { CASE_KIND_LABEL, CASE_STATE_LABEL } from '@shared/lib/labels';
import { P } from '@shared/lib/permissions';
import { pageVisible } from '@shared/lib/visibility';
import { Icon } from '@shared/ui/icon';
import { LoadMore } from '@shared/ui/load-more';
import { PageHead } from '@shared/ui/page-head';
import { TabItem, Tabs } from '@shared/ui/tabs';
import { ToastService } from '@shared/ui/toast';
import { ModerationCaseView } from '@widgets/moderation-case-view/moderation-case-view';

type ModerationTab = 'queue' | 'bulk';

const PAGE_LIMIT = 20;
const QUEUE_REFRESH_MS = 60_000;
const STATES: ModerationCaseState[] = ['open', 'approved', 'rejected', 'dismissed'];
const KINDS = Object.keys(CASE_KIND_LABEL) as ModerationCaseKind[];

/** Очередь модерации и пакетный отзыв наблюдений. */
@Component({
  selector: 'app-moderation-page',
  imports: [Icon, PageHead, Tabs, LoadMore, ModerationCaseView, BulkRetractForm],
  template: `
    <div class="page">
      <app-page-head
        title="Модерация"
        lead="Сообщения, которые система не смогла разобрать уверенно, а также отметки для снятия. Каждое действие попадает в журнал аудита."
      >
        @if (tab() === 'queue') {
          <button class="btn" type="button" [disabled]="loading()" (click)="refresh()">Обновить</button>
        }
      </app-page-head>

      <app-tabs [tabs]="tabs()" [(active)]="tab" />

      @if (tab() === 'bulk') {
        <app-bulk-retract-form />
      } @else {
        @if (hasOpenAnomalies()) {
          <p class="callout callout--info">
            <app-icon name="alert" />
            <span>Обнаружен всплеск отметок — проверьте кейсы аномалий</span>
          </p>
        }

        <div class="row filters">
          <div class="field">
            <label for="mod-state">Состояние</label>
            <select id="mod-state" [value]="state()" (change)="pickState($any($event.target).value)">
              @for (option of states; track option) {
                <option [value]="option">{{ stateLabel[option] }}</option>
              }
            </select>
          </div>
          <div class="field">
            <label for="mod-kind">Вид кейса</label>
            <select id="mod-kind" [value]="kind()" (change)="pickKind($any($event.target).value)">
              <option value="">Все виды</option>
              @for (option of kinds; track option) {
                <option [value]="option">{{ kindLabel[option] }}</option>
              }
            </select>
          </div>
          <span class="grow"></span>
          <small>Кейсов в загруженной очереди: {{ items().length }}</small>
        </div>

        @if (error(); as message) {
          <p class="callout"><app-icon name="alert" /><span>{{ message }}</span></p>
        }

        <div class="split">
          <div class="stack queue">
            @for (c of items(); track c.id) {
              <button type="button" class="case" [class.is-active]="c.id === selectedId()" (click)="select(c.id)">
                <span class="row">
                  <span class="chip" [class.chip--alert]="isAlert(c.kind)">{{ kindLabel[c.kind] }}</span>
                  <small class="grow right">{{ ago(c.createdAt) }}</small>
                </span>
                @if (c.sourceName) { <strong>{{ c.sourceName }}</strong> }
                @if (c.mapObjectName) { <small>Фиксация: {{ c.mapObjectName }}</small> }
                @if (c.messageExcerpt) { <span class="excerpt">{{ c.messageExcerpt }}</span> }
              </button>
            } @empty {
              @if (loading()) {
                <p class="muted">Загрузка…</p>
              } @else {
                <div class="card empty">Очередь пуста.</div>
              }
            }
            <app-load-more [hasMore]="!!nextCursor()" [loading]="moreLoading()" (more)="loadMore()" />
          </div>

          <div>
            @if (selectedId(); as id) {
              <app-moderation-case-view [caseId]="id" (resolved)="onResolved($event)" />
            } @else {
              <div class="card empty">Выберите кейс из очереди.</div>
            }
          </div>
        </div>
      }
    </div>`,
  styles: `
    .filters { align-items: flex-end; }
    .filters .field { min-width: 180px; }
    .queue { align-content: start; }
    .case { display: grid; gap: 6px; width: 100%; padding: 12px 14px; text-align: left; cursor: pointer;
      color: var(--ink); background: var(--surface); border: 1px solid var(--line-soft);
      border-radius: var(--r-lg); }
    .case:hover { background: var(--surface-2); }
    .case.is-active { border-color: var(--brand); background: var(--brand-soft); }
    .case blockquote, .case .excerpt { margin: 0; padding-left: 10px; border-left: 3px solid var(--line);
      color: var(--ink-2); font-size: 14px; }
    .case .excerpt { display: block; }
    .case .right { text-align: right; }
    .chip--alert { background: color-mix(in srgb, var(--st-none) 14%, var(--surface)); color: var(--st-none); }
  `,
})
export class ModerationPage {
  private readonly api = inject(ModerationApi);
  private readonly session = inject(SessionService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly toasts = inject(ToastService);

  protected readonly kindLabel = CASE_KIND_LABEL;
  protected readonly stateLabel = CASE_STATE_LABEL;
  protected readonly states = STATES;
  protected readonly kinds = KINDS;
  protected readonly ago = agoIso;

  private readonly initial = this.route.snapshot.queryParamMap;

  protected readonly canRetract = computed(() => this.session.has(P.observationRetract));
  protected readonly tab = signal<ModerationTab>(
    this.initial.get('tab') === 'bulk' && this.session.has(P.observationRetract) ? 'bulk' : 'queue',
  );
  protected readonly state = signal<ModerationCaseState>(this.readState(this.initial.get('state')));
  protected readonly kind = signal<ModerationCaseKind | ''>(this.readKind(this.initial.get('kind')));
  protected readonly selectedId = signal<string | null>(this.initial.get('case'));

  protected readonly tabs = computed<TabItem[]>(() =>
    this.canRetract()
      ? [
          { id: 'queue', label: 'Очередь', icon: 'inbox' },
          { id: 'bulk', label: 'Пакетный отзыв', icon: 'trash' },
        ]
      : [{ id: 'queue', label: 'Очередь', icon: 'inbox' }],
  );

  protected readonly items = signal<ModerationCaseSummary[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(true);
  protected readonly moreLoading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly hasOpenAnomalies = computed(() =>
    this.items().some((item) => item.kind === 'anomaly' && item.state === 'open'),
  );

  private seq = 0;

  constructor() {
    effect(() => {
      this.state();
      this.kind();
      untracked(() => void this.load(true));
    });

    effect(() => {
      const params: Params = {};
      if (this.tab() !== 'queue') params['tab'] = this.tab();
      if (this.state() !== 'open') params['state'] = this.state();
      if (this.kind()) params['kind'] = this.kind();
      if (this.selectedId()) params['case'] = this.selectedId();
      void this.router.navigate([], {
        relativeTo: this.route,
        queryParams: params,
        replaceUrl: true,
      });
    });

    const visible = pageVisible();
    const timer = setInterval(() => {
      if (!visible() || this.tab() !== 'queue' || this.loading() || this.moreLoading()) return;
      void this.load(true, true);
    }, QUEUE_REFRESH_MS);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  protected isAlert(kind: ModerationCaseKind): boolean {
    return kind === 'anomaly' || kind === 'complaint';
  }

  protected select(id: string): void {
    this.selectedId.set(id);
  }

  protected pickState(value: string): void {
    if (STATES.includes(value as ModerationCaseState)) {
      this.state.set(value as ModerationCaseState);
    }
  }

  protected pickKind(value: string): void {
    this.kind.set(value === '' ? '' : (value as ModerationCaseKind));
  }

  protected refresh(): void {
    void this.load(true, this.items().length > 0);
  }

  protected onResolved(updated: ModerationCase): void {
    const summary: ModerationCaseSummary = {
      id: updated.id,
      kind: updated.kind,
      state: updated.state,
      createdAt: updated.createdAt,
      sourceName: updated.sourceName ?? null,
      mapObjectName: updated.mapObjectName ?? null,
      messageExcerpt: updated.messageExcerpt ?? null,
    };

    const fitsState = this.state() === updated.state;
    const fitsKind = !this.kind() || this.kind() === updated.kind;
    if (fitsState && fitsKind) {
      this.items.update((list) =>
        list.map((item) => (item.id === updated.id ? summary : item)),
      );
      return;
    }

    const index = this.items().findIndex((item) => item.id === updated.id);
    if (index < 0) return;
    const rest = this.items().filter((item) => item.id !== updated.id);
    this.items.set(rest);
    const next = rest[index] ?? rest[index - 1] ?? null;
    this.selectedId.set(next?.id ?? null);
  }

  protected async loadMore(): Promise<void> {
    const cursor = this.nextCursor();
    if (!cursor || this.moreLoading()) return;

    const seq = this.seq;
    this.moreLoading.set(true);
    try {
      const page = await this.api.list({
        state: this.state(),
        kind: this.kind() || undefined,
        cursor,
        limit: PAGE_LIMIT,
      });
      if (seq !== this.seq) return;
      const seen = new Set(this.items().map((item) => item.id));
      this.items.update((list) => [...list, ...page.items.filter((item) => !seen.has(item.id))]);
      this.nextCursor.set(page.nextCursor);
    } catch (err) {
      this.toasts.fromError(err);
    } finally {
      this.moreLoading.set(false);
    }
  }

  private readState(value: string | null): ModerationCaseState {
    return STATES.includes(value as ModerationCaseState)
      ? (value as ModerationCaseState)
      : 'open';
  }

  private readKind(value: string | null): ModerationCaseKind | '' {
    return KINDS.includes(value as ModerationCaseKind) ? (value as ModerationCaseKind) : '';
  }

  private async load(reset: boolean, silent = false): Promise<void> {
    const seq = ++this.seq;
    if (reset && !silent) {
      this.items.set([]);
      this.nextCursor.set(null);
      this.loading.set(true);
    }
    this.error.set(null);
    try {
      const page = await this.api.list({
        state: this.state(),
        kind: this.kind() || undefined,
        limit: PAGE_LIMIT,
      });
      if (seq !== this.seq) return;
      this.items.set(page.items);
      this.nextCursor.set(page.nextCursor);
    } catch (err) {
      if (seq !== this.seq) return;
      this.error.set(errorText(toApiError(err)));
    } finally {
      if (seq === this.seq && !silent) this.loading.set(false);
    }
  }
}
