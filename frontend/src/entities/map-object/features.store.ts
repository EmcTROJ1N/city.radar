import { DestroyRef, Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { errorText, toApiError } from '@shared/api/api-error';
import type { CoverageKind, MapFeature, StatusView } from '@shared/api/types';
import { environment } from '@env/environment';
import { storage } from '@shared/lib/storage';
import { pageVisible } from '@shared/lib/visibility';
import { ToastService } from '@shared/ui/toast';
import { MapApi, ListFeaturesQuery } from './api';
import { bboxParam, MapView } from '@shared/map-adapter/map-adapter';

export interface MapFilters {
  /** Пусто — всё. */
  kinds: CoverageKind[];
  categories: string[];
  /** Коды статусов из справочника; пусто — все. */
  statuses: string[];
  /** Идентификаторы скрытых групп; `'none'` — фиксации без группы. */
  hiddenGroups: string[];
}

export const DEFAULT_MAP_FILTERS: MapFilters = {
  kinds: [],
  categories: [],
  statuses: [],
  hiddenGroups: [],
};

const CACHE_KEY = 'mayak.map.last';

interface CachedMap {
  items: MapFeature[];
  generatedAt: string | null;
}

function readCache(): CachedMap | null {
  const raw = storage.get(CACHE_KEY);
  if (!raw) return null;
  try {
    const parsed: unknown = JSON.parse(raw);
    if (!parsed || typeof parsed !== 'object') return null;
    const record = parsed as Record<string, unknown>;
    if (!Array.isArray(record['items'])) return null;
    return {
      items: record['items'] as MapFeature[],
      generatedAt: typeof record['generatedAt'] === 'string' ? record['generatedAt'] : null,
    };
  } catch {
    return null;
  }
}

/** Состояние публичной карты: bbox-опрос с ETag, фильтры и офлайн-кэш. */
@Injectable()
export class MapFeaturesStore {
  private readonly api = inject(MapApi);
  private readonly toasts = inject(ToastService);
  private readonly visible = pageVisible();

  readonly view = signal<MapView | null>(null);
  readonly filters = signal<MapFilters>({ ...DEFAULT_MAP_FILTERS });
  readonly items = signal<MapFeature[]>([]);
  readonly generatedAt = signal<string | null>(null);
  readonly fromCache = signal(false);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  readonly count = computed(() => this.items().length);

  private readonly knownGroups = signal<string[]>([]);
  private etagKey: string | null = null;
  private etag: string | null = null;
  private seq = 0;
  private pausedUntil = 0;
  private lastErrorText: string | null = null;
  private soonTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    const cached = readCache();
    if (cached) {
      this.items.set(cached.items);
      this.generatedAt.set(cached.generatedAt);
      this.fromCache.set(true);
    }

    effect((onCleanup) => {
      const visible = this.visible();
      const view = this.view();
      this.filters();
      if (!visible || !view) return;

      untracked(() => void this.refresh());
      const timer = setInterval(() => untracked(() => void this.refresh()), environment.pollIntervalMs);
      onCleanup(() => clearInterval(timer));
    });

    inject(DestroyRef).onDestroy(() => {
      this.seq++;
      if (this.soonTimer) {
        clearTimeout(this.soonTimer);
        this.soonTimer = null;
      }
    });
  }

  /** Известные группы нужны, чтобы фильтр «скрытые» не прятал новые группы. */
  setKnownGroups(ids: string[]): void {
    this.knownGroups.set([...new Set(ids)]);
  }

  /** Локально применяет пересчитанный статус: ETag не трогаем, проекция карты обновится позже. */
  applyStatus(mapObjectId: string, status: StatusView | null): void {
    this.items.update((items) =>
      items.map((item) => (item.id === mapObjectId ? { ...item, status } : item)),
    );
  }

  /** Однократный отложенный опрос: перекрывает гонку с фоновым пересчётом проекции карты. */
  refreshSoon(delayMs = 1500): void {
    if (this.soonTimer) clearTimeout(this.soonTimer);
    this.soonTimer = setTimeout(() => {
      this.soonTimer = null;
      void this.refresh();
    }, delayMs);
  }

  refresh(): Promise<void> {
    return this.load();
  }

  private async load(): Promise<void> {
    const view = this.view();
    if (!view || Date.now() < this.pausedUntil) return;

    const query = this.buildQuery(view, this.filters());
    const key = JSON.stringify(query);
    const etag = this.etagKey === key ? this.etag : null;
    const seq = ++this.seq;

    this.loading.set(true);
    try {
      const result = await this.api.listFeatures(query, etag);
      if (seq !== this.seq) return;
      if (result.notModified) return;

      this.etagKey = key;
      this.etag = result.etag;
      this.items.set(result.body.items);
      this.generatedAt.set(result.body.generatedAt);
      this.fromCache.set(false);
      this.error.set(null);
      this.lastErrorText = null;
      this.cache(result.body.items, result.body.generatedAt);
    } catch (err) {
      if (seq !== this.seq) return;
      const error = toApiError(err);
      if (error.status === 0) {
        this.fromCache.set(true);
        this.error.set(null);
      } else {
        this.fail(errorText(error));
        if (error.status === 429) {
          this.pausedUntil = Date.now() + (error.retryAfter ?? 60) * 1000;
        }
      }
    } finally {
      if (seq === this.seq) this.loading.set(false);
    }
  }

  private buildQuery(view: MapView, filters: MapFilters): ListFeaturesQuery {
    const query: ListFeaturesQuery = {
      bbox: bboxParam(view.bbox),
      zoom: Math.round(view.zoom),
    };
    if (filters.kinds.length) query.kinds = filters.kinds;
    if (filters.categories.length) query.categories = filters.categories;
    if (filters.statuses.length) query.statuses = filters.statuses;
    const groups = this.visibleGroups(filters);
    if (groups) query.groups = groups;
    return query;
  }

  /** Пустой список скрытых — параметр не передаётся: видны все, включая новые группы. */
  private visibleGroups(filters: MapFilters): string[] | undefined {
    if (filters.hiddenGroups.length === 0) return undefined;
    return [...this.knownGroups(), 'none'].filter((id) => !filters.hiddenGroups.includes(id));
  }

  private cache(items: MapFeature[], generatedAt: string): void {
    const payload: CachedMap = { items, generatedAt };
    storage.set(CACHE_KEY, JSON.stringify(payload));
  }

  private fail(text: string): void {
    this.error.set(text);
    if (this.lastErrorText !== text) {
      this.lastErrorText = text;
      this.toasts.error(text);
    }
  }
}
