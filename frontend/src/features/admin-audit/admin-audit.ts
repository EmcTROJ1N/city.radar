import { Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { AdminApi } from '@entities/user';
import { ApiError, toApiError } from '@shared/api/api-error';
import type { AuditEntry } from '@shared/api/types';
import { dateTime } from '@shared/lib/format';
import { EntityPicker, PickerLoader } from '@shared/ui/entity-picker';
import { LoadMore } from '@shared/ui/load-more';
import { ToastService } from '@shared/ui/toast';
import { changedKeys, jsonLines } from './audit-diff';

const PAGE_SIZE = 50;

interface AuditFilters {
  actorId: string;
  action: string;
  entityType: string;
  entityId: string;
  from: string;
  to: string;
}

const EMPTY: AuditFilters = { actorId: '', action: '', entityType: '', entityId: '', from: '', to: '' };

function pad(value: number): string {
  return String(value).padStart(2, '0');
}

/** ISO из адреса → значение для input[type=datetime-local] в локальном времени. */
function toLocalInput(iso: string | null): string {
  if (!iso) return '';
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function toIso(local: string): string | null {
  if (!local) return null;
  const date = new Date(local);
  return Number.isNaN(date.getTime()) ? null : date.toISOString();
}

/** Журнал аудита: только чтение, фильтры живут в адресе страницы. */
@Component({
  selector: 'app-admin-audit',
  imports: [EntityPicker, LoadMore],
  template: `
    <div class="card stack">
      <form class="stack" (submit)="apply($event)">
        <div class="row filters">
          <div class="field grow">
            <span>Пользователь</span>
            <app-entity-picker
              [loader]="loadActors"
              [labels]="actorLabels()"
              [(value)]="actorIds"
              placeholder="Любой"
            />
          </div>
          <label class="field grow">
            <span>Действие</span>
            <input
              [value]="filters().action"
              (input)="setFilter('action', $event)"
              placeholder="Например: source.update"
              autocomplete="off"
            />
          </label>
        </div>

        <div class="row filters">
          <label class="field grow">
            <span>Тип объекта</span>
            <input [value]="filters().entityType" (input)="setFilter('entityType', $event)" autocomplete="off" />
          </label>
          <label class="field grow">
            <span>Идентификатор объекта</span>
            <input [value]="filters().entityId" (input)="setFilter('entityId', $event)" autocomplete="off" />
          </label>
        </div>

        <div class="row filters">
          <label class="field grow">
            <span>Период с</span>
            <input type="datetime-local" [value]="filters().from" (input)="setFilter('from', $event)" />
          </label>
          <label class="field grow">
            <span>Период по</span>
            <input type="datetime-local" [value]="filters().to" (input)="setFilter('to', $event)" />
          </label>
          <div class="row">
            <button class="btn btn--primary" type="submit" [disabled]="loading()">Показать</button>
            <button class="btn btn--ghost" type="button" (click)="reset()">Сбросить</button>
          </div>
        </div>
      </form>
    </div>

    <div class="card card--flush table-wrap table-wrap--cards">
      <table class="table table--cards">
        <thead>
          <tr><th>Время</th><th>Кто</th><th>Действие</th><th>Объект</th><th>Причина</th></tr>
        </thead>
        <tbody>
          @if (loading() && !items().length) {
            <tr><td colspan="5" class="empty">Загрузка…</td></tr>
          } @else if (error() && !items().length) {
            <tr><td colspan="5" class="empty">Не удалось загрузить журнал.</td></tr>
          } @else {
            @for (entry of items(); track entry.id) {
              <tr class="is-clickable" [class.is-active]="expanded() === entry.id" (click)="toggle(entry)">
                <td class="nowrap" data-label="Время">{{ at(entry.at) }}</td>
                <td data-label="Кто">{{ who(entry) }}</td>
                <td data-label="Действие"><code>{{ entry.action }}</code></td>
                <td data-label="Объект">{{ object(entry) }}</td>
                <td class="reason" data-label="Причина">{{ entry.reason ?? '—' }}</td>
              </tr>
              @if (expanded() === entry.id) {
                <tr class="details-row">
                  <td colspan="5" class="details">
                    <div class="stack">
                      <div class="changed">Изменённые ключи: {{ changedList() || '—' }}</div>
                      <div class="json-grid">
                        <div>
                          <h3>Было</h3>
                          @if (beforeLines().length) {
                            <pre>@for (line of beforeLines(); track $index) {<span class="ln" [class.is-changed]="line.changed">{{ line.text }}</span>}</pre>
                          } @else {
                            <p class="hint">Нет данных</p>
                          }
                        </div>
                        <div>
                          <h3>Стало</h3>
                          @if (afterLines().length) {
                            <pre>@for (line of afterLines(); track $index) {<span class="ln" [class.is-changed]="line.changed">{{ line.text }}</span>}</pre>
                          } @else {
                            <p class="hint">Нет данных</p>
                          }
                        </div>
                      </div>
                      <dl class="kv">
                        <dt>IP</dt>
                        <dd>{{ entry.ip ?? '—' }}</dd>
                        <dt>User-Agent</dt>
                        <dd>{{ entry.userAgent ?? '—' }}</dd>
                      </dl>
                    </div>
                  </td>
                </tr>
              }
            } @empty {
              <tr><td colspan="5" class="empty">Записей нет.</td></tr>
            }
          }
        </tbody>
      </table>
    </div>

    <app-load-more [hasMore]="nextCursor() !== null" [loading]="loading()" (more)="more()" />`,
  styles: `
    :host { display: grid; gap: var(--gap); }
    .filters { align-items: flex-end; }
    .nowrap { white-space: nowrap; }
    .reason { max-width: 320px; }
    code { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 13px; }
    .details-row td { background: var(--surface-2); }
    .json-grid { display: grid; gap: 12px; grid-template-columns: 1fr 1fr; }
    .json-grid h3 { margin: 0 0 6px; font-size: 13px; color: var(--ink-3); }
    .json-grid pre { margin: 0; padding: 10px; overflow-x: auto; background: var(--surface);
      border: 1px solid var(--line); border-radius: var(--r-sm); font-size: 12px; }
    .ln { display: block; }
    .ln.is-changed { background: color-mix(in srgb, var(--st-limited) 28%, transparent); }
    .changed { font-size: 13px; color: var(--ink-2); }
    @media (max-width: 800px) { .json-grid { grid-template-columns: 1fr; } }
    @media (max-width: 760px) {
      .json-grid pre { white-space: pre-wrap; overflow-wrap: anywhere; overflow-x: hidden; }
    }
  `,
})
export class AdminAudit {
  private readonly api = inject(AdminApi);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly toasts = inject(ToastService);

  private readonly initial = this.readQuery();
  protected readonly filters = signal<AuditFilters>({ ...this.initial });
  protected readonly applied = signal<AuditFilters>({ ...this.initial });
  protected readonly actorIds = signal<string[]>(this.initial.actorId ? [this.initial.actorId] : []);
  protected readonly actorLabels = signal<Record<string, string>>({});
  protected readonly items = signal<AuditEntry[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly expanded = signal<number | null>(null);

  protected readonly expandedEntry = computed(
    () => this.items().find((entry) => entry.id === this.expanded()) ?? null,
  );
  protected readonly changed = computed(() =>
    changedKeys(this.expandedEntry()?.before, this.expandedEntry()?.after),
  );
  protected readonly changedList = computed(() => this.changed().join(', '));
  protected readonly beforeLines = computed(() =>
    jsonLines(this.expandedEntry()?.before ?? null, this.changed()),
  );
  protected readonly afterLines = computed(() =>
    jsonLines(this.expandedEntry()?.after ?? null, this.changed()),
  );

  private token = 0;

  protected readonly loadActors: PickerLoader = async (q, cursor) => {
    const page = await this.api.listUsers({ limit: 20, cursor: cursor ?? undefined, q: q || undefined });
    const labels = { ...this.actorLabels() };
    for (const user of page.items) labels[user.id] = user.displayName;
    this.actorLabels.set(labels);
    return {
      items: page.items.map((user) => ({
        value: user.id,
        label: user.displayName,
        hint: String(user.maxUserId),
      })),
      nextCursor: page.nextCursor,
    };
  };

  constructor() {
    if (this.initial.actorId) void this.resolveActor(this.initial.actorId);
    void this.load(null);
  }

  protected at(iso: string): string {
    return dateTime(iso);
  }

  protected who(entry: AuditEntry): string {
    return entry.actorName ?? entry.actorId ?? 'Система';
  }

  protected object(entry: AuditEntry): string {
    if (!entry.entityType && !entry.entityId) return '—';
    return `${entry.entityType ?? '—'} · ${entry.entityId ?? '—'}`;
  }

  protected setFilter(field: Exclude<keyof AuditFilters, 'actorId'>, event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.filters.update((filters) => ({ ...filters, [field]: value }));
  }

  protected toggle(entry: AuditEntry): void {
    this.expanded.set(this.expanded() === entry.id ? null : entry.id);
  }

  protected apply(event: Event): void {
    event.preventDefault();
    const current = this.filters();
    const next: AuditFilters = {
      ...current,
      actorId: this.actorIds()[0] ?? '',
      action: current.action.trim(),
      entityType: current.entityType.trim(),
      entityId: current.entityId.trim(),
    };
    this.filters.set(next);
    this.applied.set(next);
    this.expanded.set(null);
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        actorId: next.actorId || null,
        action: next.action || null,
        entityType: next.entityType || null,
        entityId: next.entityId || null,
        from: toIso(next.from),
        to: toIso(next.to),
      },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
    void this.reload();
  }

  protected reset(): void {
    this.filters.set({ ...EMPTY });
    this.applied.set({ ...EMPTY });
    this.actorIds.set([]);
    this.expanded.set(null);
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { actorId: null, action: null, entityType: null, entityId: null, from: null, to: null },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
    void this.reload();
  }

  protected async more(): Promise<void> {
    const cursor = this.nextCursor();
    if (!cursor || this.loading()) return;
    await this.load(cursor);
  }

  private async reload(): Promise<void> {
    this.items.set([]);
    this.nextCursor.set(null);
    await this.load(null);
  }

  private async load(cursor: string | null): Promise<void> {
    const token = ++this.token;
    const filters = this.applied();
    this.loading.set(true);
    this.error.set(null);
    try {
      const page = await this.api.listAudit({
        limit: PAGE_SIZE,
        cursor: cursor ?? undefined,
        actorId: filters.actorId || undefined,
        action: filters.action || undefined,
        entityType: filters.entityType || undefined,
        entityId: filters.entityId || undefined,
        from: toIso(filters.from) ?? undefined,
        to: toIso(filters.to) ?? undefined,
      });
      if (token !== this.token) return;
      this.items.update((list) => (cursor ? [...list, ...page.items] : page.items));
      this.nextCursor.set(page.nextCursor);
    } catch (err) {
      if (token !== this.token) return;
      this.error.set(toApiError(err));
      this.toasts.fromError(err);
    } finally {
      if (token === this.token) this.loading.set(false);
    }
  }

  private readQuery(): AuditFilters {
    const params = this.route.snapshot.queryParamMap;
    return {
      actorId: params.get('actorId') ?? '',
      action: params.get('action') ?? '',
      entityType: params.get('entityType') ?? '',
      entityId: params.get('entityId') ?? '',
      from: toLocalInput(params.get('from')),
      to: toLocalInput(params.get('to')),
    };
  }

  private async resolveActor(id: string): Promise<void> {
    try {
      const page = await this.api.listUsers({ limit: 100 });
      const user = page.items.find((item) => item.id === id);
      if (user) this.actorLabels.update((labels) => ({ ...labels, [id]: user.displayName }));
    } catch {
      // Подпись необязательна: в поле останется идентификатор.
    }
  }
}
