import { Component, DestroyRef, ElementRef, computed, effect, inject, signal, viewChild } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { CategoriesApi } from '@entities/category';
import { SourcesApi, sourceStateIcon, sourceStateTone, type ListSourcesQuery } from '@entities/source';
import { SourceForm } from '@features/source-edit/source-form';
import { SOURCE_TYPE_FILTER_OPTIONS } from '@features/source-edit/source-form.model';
import { SourceStatuses } from '@features/source-edit/source-statuses';
import { SourceHealthPanel } from '@features/source-health/source-health';
import { ApiError, errorText, toApiError } from '@shared/api/api-error';
import type { Source, SourceHealth, SourceStatus, SourceType } from '@shared/api/types';
import { closeDialog, openDialog } from '@shared/lib/dialog';
import { agoIso, durationSec } from '@shared/lib/format';
import { SOURCE_STATE_LABEL, SOURCE_TYPE_LABEL, TELEGRAM_MODE_SHORT } from '@shared/lib/labels';
import { ConfirmService } from '@shared/ui/confirm';
import { Icon, normalizeIcon } from '@shared/ui/icon';
import { LoadMore } from '@shared/ui/load-more';
import { PageHead } from '@shared/ui/page-head';
import { SearchInput } from '@shared/ui/search-input';
import { StatusBadge } from '@shared/ui/status-badge';
import { Tabs, type TabItem } from '@shared/ui/tabs';
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
    Tabs,
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

          <div class="card card--flush table-wrap table-wrap--cards">
            @if (items().length) {
              <table class="table table--cards">
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
                      <td class="cell-main">
                        <strong>{{ source.name }}</strong>
                        <div>
                          <small>
                            @if (isSite(source)) {
                              Системный · {{ categoryName(source) }}
                            } @else {
                              {{ typeLabels[source.sourceType] }} · {{ source.chatId }}
                              @if (source.mode) { · {{ modeLabels[source.mode] }} }
                            }
                            @if (!source.isEnabled) { · выключен }
                          </small>
                        </div>
                      </td>
                      <td data-label="Статусы">
                        <div class="statuses" (mouseenter)="showStatuses($event, source)" (mouseleave)="hideStatuses()">
                          @for (status of source.statuses; track status.id) {
                            <span class="statuses__dot" [class]="'tone-' + status.color" [title]="status.name"></span>
                          } @empty {
                            <small>—</small>
                          }
                        </div>
                      </td>
                      <td class="num" data-label="Таймаут">{{ timeout(source.timeoutSeconds) }}</td>
                      <td data-label="Состояние">
                        <app-status-badge [label]="stateLabels[source.state]" [tone]="stateTone(source.state)"
                          [icon]="stateIcon(source.state)" />
                      </td>
                      <td class="num" data-label="Последнее сообщение">{{ source.lastMessageAt ? ago(source.lastMessageAt) : '—' }}</td>
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
          @if (selected(); as source) {
            <!-- Сводка выбранного источника; всё редактирование — в окне настроек. -->
            <section class="card stack summary" aria-labelledby="source-summary-title">
              <header class="row head">
                <h2 id="source-summary-title" class="grow">{{ source.name }}</h2>
                @if (!source.isEnabled) { <span class="chip">Выключен</span> }
              </header>
              <dl class="kv facts">
                <dt>Тип</dt>
                <dd>
                  @if (isSite(source)) {
                    Системный · {{ categoryName(source) }}
                  } @else {
                    {{ typeLabels[source.sourceType] }}@if (source.mode) { · {{ modeLabels[source.mode] }} }
                  }
                </dd>
                @if (!isSite(source)) {
                  <dt>Чат</dt>
                  <dd>{{ source.chatId }}</dd>
                }
                <dt>Таймаут</dt>
                <dd>{{ timeout(source.timeoutSeconds) }}</dd>
                <dt>Статусы</dt>
                <dd class="badges">
                  @for (status of source.statuses; track status.id) {
                    <app-status-badge [label]="status.name" [tone]="status.color" [icon]="iconName(status.icon)" />
                  } @empty {
                    <span class="muted">Нет — добавьте в настройках</span>
                  }
                </dd>
              </dl>
              <div class="card-actions">
                <button class="btn btn--primary" type="button" (click)="openSettings()">
                  <app-icon name="settings" />Настройки
                </button>
                <div class="card-actions__row">
                  <button class="btn" type="button" [disabled]="busyId() === source.id" (click)="toggle(source)">
                    <app-icon [name]="source.isEnabled ? 'cross' : 'check'" />
                    {{ source.isEnabled ? 'Выключить' : 'Включить' }}
                  </button>
                  @if (!isSite(source)) {
                    <button class="btn btn--ghost btn--danger" type="button" (click)="remove(source)">
                      <app-icon name="trash" />Удалить
                    </button>
                  }
                </div>
              </div>
            </section>

            <app-source-health [sourceId]="source.id" (refreshed)="onHealth(source.id, $event)" />
          } @else {
            <div class="card empty">Выберите источник в списке или создайте новый.</div>
          }
        </div>
      </div>

      <!-- Настроек у источника много: форма открывается отдельным окном, а не в узкой колонке. -->
      <dialog #settings class="modal" aria-labelledby="source-settings-title" (cancel)="onSettingsCancel($event)"
        (close)="closeSettings()">
        @if (settingsOpen() && (creating() || selected())) {
          <div class="modal__box stack">
            <header class="row head">
              <div class="grow">
                <h2 id="source-settings-title">{{ creating() ? 'Новый источник' : 'Настройки источника' }}</h2>
                @if (selected(); as source) { <small>{{ source.name }}</small> }
              </div>
              <button class="btn btn--ghost btn--icon" type="button" aria-label="Закрыть окно" (click)="closeSettings()">
                <app-icon name="cross" />
              </button>
            </header>
            @if (selected(); as source) {
              <app-tabs [tabs]="settingsTabs" [(active)]="settingsTab" />
              <!-- Вкладки скрываются, а не пересоздаются: несохранённые правки формы не теряются. -->
              <div [hidden]="settingsTab() !== 'main'">
                <app-source-form [source]="source" (saved)="onSaved($event)" (cancelled)="closeSettings()" />
              </div>
              <div [hidden]="settingsTab() !== 'statuses'">
                <app-source-statuses [source]="source" (statusesChange)="onStatuses(source.id, $event)" />
              </div>
            } @else {
              <app-source-form [source]="null" (saved)="onSaved($event)" (cancelled)="closeSettings()" />
            }
          </div>
        }
      </dialog>

      @if (statusPop(); as pop) {
        <div class="statuses__pop" role="tooltip" [style.left.px]="pop.x" [style.top.px]="pop.y"
          (mouseenter)="keepStatuses()" (mouseleave)="hideStatuses()">
          @for (status of pop.items; track status.id) {
            <app-status-badge [label]="status.name" [tone]="status.color" [icon]="iconName(status.icon)" />
          }
        </div>
      }
    </div>`,
  styles: `
    .filters { display: grid; grid-template-columns: minmax(180px, 1fr) max-content max-content; gap: 8px; align-items: end; padding: 12px; }
    .summary { min-width: 0; overflow-wrap: anywhere; }
    .summary h2 { font-size: 20px; }
    .facts { grid-template-columns: 84px minmax(0, 1fr); align-items: start; }
    .facts dd { min-height: 22px; }
    .badges { display: flex; flex-wrap: wrap; gap: 6px; }
    // Действия — как в карточке объекта на карте: крупные кнопки, главная — «Настройки» на всю ширину.
    // Под ней остальные кнопки делят строку поровну.
    .card-actions { display: grid; gap: 8px; padding-top: 12px; border-top: 1px solid var(--line-soft); }
    .card-actions__row { display: flex; gap: 8px; }
    .card-actions .btn { justify-content: center; white-space: nowrap; }
    .card-actions__row .btn { flex: 1 1 0; min-width: 0; }
    .modal { width: min(760px, calc(100vw - 32px)); max-width: none; max-height: 90vh; padding: 0; border: 0;
      background: transparent; overflow: visible; }
    .modal::backdrop { background: rgb(15 42 51 / 0.45); }
    .modal__box { max-height: 90vh; overflow: auto; padding: var(--pad); background: var(--surface);
      border: 1px solid var(--line); border-radius: var(--r-lg); box-shadow: var(--shadow-float); min-width: 0; }
    .modal__box > *, .modal__box .row > * { min-width: 0; }
    .head { align-items: flex-start; flex-wrap: nowrap; }
    .head small { overflow-wrap: anywhere; }
    // Список стоит в узкой колонке рядом с карточкой источника: длинные заголовки переносим, иначе появляется горизонтальный скролл.
    .table th { white-space: normal; }
    // Статусы в списке — компактные точки; полные плашки показываются при наведении.
    .statuses { display: inline-flex; align-items: center; gap: 5px; cursor: default; }
    .statuses__dot { width: 10px; height: 10px; border-radius: 50%; background: var(--tone, var(--st-stale)); flex: none; }
    .statuses__pop { position: fixed; z-index: 60; display: grid; gap: 4px; padding: 8px;
      border-radius: var(--r-md); background: var(--surface); border: 1px solid var(--line); box-shadow: var(--shadow-float); }
    @media (min-width: 761px) and (max-width: 900px) {
      // На планшетах прячем второстепенные колонки, чтобы список не расползался.
      // На телефонах таблица превращается в карточки и показывает все поля.
      .table th:nth-child(3), .table td:nth-child(3),
      .table th:nth-child(5), .table td:nth-child(5) { display: none; }
    }
    @media (max-width: 700px) {
      .filters { grid-template-columns: 1fr; }
    }
    @media (max-width: 640px) {
      .modal { width: 100vw; height: 100dvh; max-height: none; margin: 0; }
      .modal__box { height: 100%; max-height: none; border-radius: 0; border: 0; }
    }
  `,
})
export class SourcesPage {
  private readonly api = inject(SourcesApi);
  private readonly categoriesApi = inject(CategoriesApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly toasts = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  protected readonly types = SOURCE_TYPE_FILTER_OPTIONS;
  protected readonly typeLabels = SOURCE_TYPE_LABEL;
  protected readonly modeLabels = TELEGRAM_MODE_SHORT;
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
  protected readonly categories = signal<Record<string, string>>({});

  /** Плавающая подсказка со статусами источника — показывается по наведению. */
  protected readonly statusPop = signal<{ x: number; y: number; items: SourceStatus[] } | null>(null);
  private popTimer: ReturnType<typeof setTimeout> | null = null;

  protected readonly selectedId = signal<string | null>(this.route.snapshot.queryParamMap.get('id'));
  protected readonly creating = computed(() => this.selectedId() === 'new');
  /** Окно настроек; ссылка ?id=new сразу открывает создание. */
  protected readonly settingsOpen = signal(this.selectedId() === 'new');
  protected readonly settingsTab = signal('main');
  protected readonly settingsTabs: TabItem[] = [
    { id: 'main', label: 'Основное', icon: 'settings' },
    { id: 'statuses', label: 'Статусы', icon: 'drop' },
  ];
  private readonly settings = viewChild<ElementRef<HTMLDialogElement>>('settings');
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
    inject(DestroyRef).onDestroy(() => this.clearPopTimer());
    void this.load();
    void this.loadCategories();
    // Выбранный источник живёт в адресе: ссылку ?id=<id> можно открыть напрямую.
    effect(() => {
      void this.router.navigate([], {
        relativeTo: this.route,
        queryParams: { id: this.selectedId() },
        replaceUrl: true,
      });
    });

    effect(() => {
      const dialog = this.settings()?.nativeElement;
      if (!dialog) return;
      if (this.settingsOpen()) openDialog(dialog);
      else closeDialog(dialog);
    });
  }

  protected create(): void {
    this.selectedId.set('new');
    this.settingsOpen.set(true);
  }

  protected openSettings(): void {
    this.settingsTab.set('main');
    this.settingsOpen.set(true);
  }

  /** Закрытие без сохранения: незавершённое создание сбрасывает выбор. */
  protected closeSettings(): void {
    this.settingsOpen.set(false);
    if (this.creating()) this.selectedId.set(null);
  }

  protected onSettingsCancel(event: Event): void {
    event.preventDefault();
    this.closeSettings();
  }

  protected isSite(source: Source): boolean {
    return source.sourceType === 'site';
  }

  /** Название категории системного «Сайта» для плашки «Системный · <категория>». */
  protected categoryName(source: Source): string {
    if (!source.categoryId) return 'без категории';
    return this.categories()[source.categoryId] ?? 'категория';
  }

  private async loadCategories(): Promise<void> {
    try {
      const items = await this.categoriesApi.list();
      this.categories.set(Object.fromEntries(items.map((item) => [item.id, item.name])));
    } catch {
      // Плашка категории не критична: список источников остаётся рабочим.
    }
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

  protected showStatuses(event: MouseEvent, source: Source): void {
    this.clearPopTimer();
    if (!source.statuses.length) return;
    const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
    this.statusPop.set({ x: rect.left, y: rect.bottom + 6, items: source.statuses });
  }

  /** Пауза перед скрытием: даёт перевести курсор на саму подсказку. */
  protected hideStatuses(): void {
    this.clearPopTimer();
    this.popTimer = setTimeout(() => {
      this.popTimer = null;
      this.statusPop.set(null);
    }, 160);
  }

  protected keepStatuses(): void {
    this.clearPopTimer();
  }

  private clearPopTimer(): void {
    if (this.popTimer) clearTimeout(this.popTimer);
    this.popTimer = null;
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
    this.settingsOpen.set(false);
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
