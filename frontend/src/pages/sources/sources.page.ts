import { Component, computed, effect, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { SourcesApi, sourceStateIcon, sourceStateTone, type ListSourcesQuery } from '@entities/source';
import { SourceForm } from '@features/source-edit/source-form';
import { SOURCE_TYPE_OPTIONS } from '@features/source-edit/source-form.model';
import { SourceStatuses } from '@features/source-edit/source-statuses';
import { SourceHealthPanel } from '@features/source-health/source-health';
import { ApiError, errorText, toApiError } from '@shared/api/api-error';
import type { Source, SourceHealth, SourceStatus, SourceType } from '@shared/api/types';
import { agoIso, durationSec } from '@shared/lib/format';
import { SOURCE_STATE_LABEL, SOURCE_TYPE_LABEL } from '@shared/lib/labels';
import { ConfirmService } from '@shared/ui/confirm';
import { Icon, normalizeIcon } from '@shared/ui/icon';
import { LoadMore } from '@shared/ui/load-more';
import { PageHead } from '@shared/ui/page-head';
import { SearchInput } from '@shared/ui/search-input';
import { StatusBadge } from '@shared/ui/status-badge';
import { ToastService } from '@shared/ui/toast';

type EnabledFilter = 'all' | 'yes' | 'no';

const PAGE_SIZE = 50;

/** Источники данных: список с фильтрами и CRUD источника. */
@Component({
  selector: 'app-sources-page',
  imports: [
    SourceForm,
    SourceStatuses,
    SourceHealthPanel,
    Icon,
    LoadMore,
    PageHead,
    SearchInput,
    StatusBadge,
  ],
  template: `
    <div class="page">
      <app-page-head title="Источники данных"
        lead="Откуда приходят статусы: чат, его статусы и промпт для разбора сообщений.">
        <button class="btn btn--primary" type="button" (click)="create()">
          <app-icon name="plus" />Создать источник
        </button>
      </app-page-head>

      <div class="split">
        <div class="stack">
          <div class="card filters">
            <app-search-input [value]="query()" (valueChange)="setQuery($event)" placeholder="Поиск по названию" />
            <div class="field">
              <label for="src-filter-type">Тип</label>
              <select id="src-filter-type" [value]="typeFilter()" (change)="setType($any($event.target).value)">
                <option value="">Все</option>
                @for (option of types; track option.value) { <option [value]="option.value">{{ option.label }}</option> }
              </select>
            </div>
            <div class="field">
              <label for="src-filter-enabled">Включён</label>
              <select id="src-filter-enabled" [value]="enabledFilter()" (change)="setEnabled($any($event.target).value)">
                <option value="all">Все</option>
                <option value="yes">Да</option>
                <option value="no">Нет</option>
              </select>
            </div>
          </div>

          @if (error(); as problem) {
            <div class="callout" role="alert">
              <app-icon name="alert" />
              <span class="grow">{{ message(problem) }}</span>
              <button class="btn btn--sm" type="button" (click)="load()">Повторить</button>
            </div>
          }

          <div class="card card--flush table-wrap">
            @if (items().length) {
              <table class="table">
                <thead>
                  <tr>
                    <th>Источник</th>
                    <th>Статусы</th>
                    <th>Таймаут</th>
                    <th>Состояние</th>
                    <th>Последнее сообщение</th>
                  </tr>
                </thead>
                <tbody>
                  @for (source of items(); track source.id) {
                    <tr class="is-clickable" tabindex="0" [class.is-active]="selectedId() === source.id"
                      (click)="select(source.id)" (keydown.enter)="select(source.id)">
                      <td>
                        <strong>{{ source.name }}</strong>
                        <div>
                          <small>{{ typeLabels[source.sourceType] }} · {{ source.chatId }}@if (!source.isEnabled) { · выключен }</small>
                        </div>
                      </td>
                      <td>
                        <div class="row">
                          @for (status of source.statuses; track status.id) {
                            <app-status-badge [label]="status.name" [tone]="status.color" [icon]="iconName(status.icon)" />
                          } @empty {
                            <small>—</small>
                          }
                        </div>
                      </td>
                      <td class="num">{{ timeout(source.timeoutSeconds) }}</td>
                      <td>
                        <app-status-badge [label]="stateLabels[source.state]" [tone]="stateTone(source.state)"
                          [icon]="stateIcon(source.state)" />
                      </td>
                      <td class="num">{{ source.lastMessageAt ? ago(source.lastMessageAt) : '—' }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            } @else {
              <div class="empty">{{ emptyText() }}</div>
            }
          </div>

          <app-load-more [hasMore]="nextCursor() !== null" [loading]="loadingMore()" (more)="loadMore()" />
        </div>

        <div class="stack">
          @if (creating()) {
            <app-source-form [source]="null" (saved)="onSaved($event)" (cancelled)="select(null)" />
          } @else if (selected(); as source) {
            <div class="card selected-head">
              <div class="row">
                <strong class="grow">{{ source.name }}</strong>
                <button class="btn btn--sm" type="button" [disabled]="busyId() === source.id" (click)="toggle(source)">
                  <app-icon [name]="source.isEnabled ? 'cross' : 'check'" />
                  {{ source.isEnabled ? 'Выключить' : 'Включить' }}
                </button>
                <button class="btn btn--sm btn--ghost btn--danger" type="button" (click)="remove(source)">
                  <app-icon name="trash" />Удалить
                </button>
              </div>
            </div>

            <app-source-health [sourceId]="source.id" (refreshed)="onHealth(source.id, $event)" />
            <app-source-form [source]="source" (saved)="onSaved($event)" (cancelled)="select(null)" />
            <app-source-statuses [source]="source" (statusesChange)="onStatuses(source.id, $event)" />
          } @else {
            <div class="card empty">Выберите источник в списке или создайте новый.</div>
          }
        </div>
      </div>
    </div>`,
  styles: `
    .filters { display: grid; grid-template-columns: minmax(180px, 1fr) max-content max-content; gap: 8px; align-items: end; padding: 12px; }
    .selected-head { padding: 10px 12px; }
    @media (max-width: 700px) {
      .filters { grid-template-columns: 1fr; }
    }
  `,
})
export class SourcesPage {
  private readonly api = inject(SourcesApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly toasts = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  protected readonly types = SOURCE_TYPE_OPTIONS;
  protected readonly typeLabels = SOURCE_TYPE_LABEL;
  protected readonly stateLabels = SOURCE_STATE_LABEL;
  protected readonly stateTone = sourceStateTone;
  protected readonly stateIcon = sourceStateIcon;
  protected readonly iconName = normalizeIcon;
  protected readonly ago = agoIso;
  protected readonly timeout = durationSec;
  protected readonly message = errorText;

  protected readonly items = signal<Source[]>([]);
  protected readonly loading = signal(false);
  protected readonly loadingMore = signal(false);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly query = signal('');
  protected readonly typeFilter = signal<SourceType | ''>('');
  protected readonly enabledFilter = signal<EnabledFilter>('all');
  protected readonly busyId = signal<string | null>(null);

  protected readonly selectedId = signal<string | null>(this.route.snapshot.queryParamMap.get('id'));
  protected readonly creating = computed(() => this.selectedId() === 'new');
  protected readonly selected = computed(() => {
    const id = this.selectedId();
    if (!id || id === 'new') return null;
    return this.items().find((source) => source.id === id) ?? null;
  });
  protected readonly hasFilters = computed(
    () => Boolean(this.query().trim()) || this.typeFilter() !== '' || this.enabledFilter() !== 'all',
  );
  protected readonly emptyText = computed(() => {
    if (this.error()) return 'Не удалось загрузить список.';
    if (this.loading()) return 'Загрузка…';
    if (this.hasFilters()) return 'Ничего не найдено. Измените фильтры.';
    return 'Пока нет источников. Создайте первый источник.';
  });

  private loadSeq = 0;

  constructor() {
    void this.load();
    // Выбранный источник живёт в адресе: ссылку ?id=<id> можно открыть напрямую.
    effect(() => {
      void this.router.navigate([], {
        relativeTo: this.route,
        queryParams: { id: this.selectedId() },
        replaceUrl: true,
      });
    });
  }

  protected create(): void {
    this.selectedId.set('new');
  }

  protected select(id: string | null): void {
    this.selectedId.set(id);
  }

  protected setQuery(value: string): void {
    this.query.set(value);
    void this.load();
  }

  protected setType(value: string): void {
    this.typeFilter.set(value as SourceType | '');
    void this.load();
  }

  protected setEnabled(value: string): void {
    this.enabledFilter.set(value as EnabledFilter);
    void this.load();
  }

  protected async load(): Promise<void> {
    const seq = ++this.loadSeq;
    this.loading.set(true);
    this.error.set(null);
    try {
      const page = await this.api.list(this.queryParams());
      if (seq !== this.loadSeq) return;
      this.items.set(page.items);
      this.nextCursor.set(page.nextCursor);
      await this.openFromLink();
    } catch (err) {
      if (seq === this.loadSeq) this.error.set(toApiError(err));
    } finally {
      if (seq === this.loadSeq) this.loading.set(false);
    }
  }

  protected async loadMore(): Promise<void> {
    const cursor = this.nextCursor();
    if (!cursor || this.loadingMore()) return;
    const seq = this.loadSeq;
    this.loadingMore.set(true);
    try {
      const page = await this.api.list({ ...this.queryParams(), cursor });
      if (seq !== this.loadSeq) return;
      this.items.update((list) => this.merge(list, page.items));
      this.nextCursor.set(page.nextCursor);
    } catch (err) {
      this.toasts.fromError(err);
    } finally {
      this.loadingMore.set(false);
    }
  }

  protected onSaved(source: Source): void {
    this.replace(source);
    this.selectedId.set(source.id);
  }

  protected onStatuses(id: string, statuses: SourceStatus[]): void {
    this.items.update((list) =>
      list.map((source) => (source.id === id ? { ...source, statuses } : source)),
    );
  }

  protected onHealth(id: string, health: SourceHealth): void {
    this.items.update((list) =>
      list.map((source) =>
        source.id === id
          ? { ...source, state: health.state, lastMessageAt: health.lastMessageAt }
          : source,
      ),
    );
  }

  protected async toggle(source: Source): Promise<void> {
    if (this.busyId()) return;
    this.busyId.set(source.id);
    try {
      const updated = source.isEnabled
        ? await this.api.disable(source.id)
        : await this.api.enable(source.id);
      this.replace(updated);
      this.toasts.success('Изменения применятся без перезапуска');
    } catch (err) {
      this.toasts.fromError(err);
    } finally {
      this.busyId.set(null);
    }
  }

  protected async remove(source: Source): Promise<void> {
    const result = await this.confirm.ask({
      title: 'Удалить источник?',
      text: `«${source.name}» и его статусы будут удалены.`,
      confirmLabel: 'Удалить',
      danger: true,
    });
    if (!result.confirmed) return;

    try {
      await this.api.remove(source.id);
      this.items.update((list) => list.filter((item) => item.id !== source.id));
      if (this.selectedId() === source.id) this.selectedId.set(null);
      this.toasts.success('Источник удалён');
    } catch (err) {
      const problem = toApiError(err);
      if (problem.status === 409) {
        this.toasts.error(
          problem.detail ?? 'По источнику есть наблюдения. Отключите источник вместо удаления',
        );
      } else {
        this.toasts.fromError(problem);
      }
    }
  }

  private queryParams(): ListSourcesQuery {
    return {
      q: this.query().trim() || undefined,
      sourceType: this.typeFilter() || undefined,
      isEnabled: this.enabledFilter() === 'all' ? undefined : this.enabledFilter() === 'yes',
      limit: PAGE_SIZE,
    };
  }

  /** Ссылка ?id=<id>: источник может быть не на первой странице — дочитываем его отдельно. */
  private async openFromLink(): Promise<void> {
    const id = this.selectedId();
    if (!id || id === 'new' || this.items().some((source) => source.id === id)) return;
    try {
      const source = await this.api.get(id);
      this.items.update((list) => (list.some((item) => item.id === id) ? list : [...list, source]));
    } catch (err) {
      this.toasts.fromError(err);
      this.selectedId.set(null);
    }
  }

  private replace(source: Source): void {
    this.items.update((list) =>
      list.some((item) => item.id === source.id)
        ? list.map((item) => (item.id === source.id ? source : item))
        : [source, ...list],
    );
  }

  private merge(list: Source[], page: Source[]): Source[] {
    const seen = new Set(list.map((source) => source.id));
    return [...list, ...page.filter((source) => !seen.has(source.id))];
  }
}
