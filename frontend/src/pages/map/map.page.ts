import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { MapApi, MapFeaturesStore } from '@entities/map-object';
import { environment } from '@env/environment';
import type {
  CreateObservationResponse,
  Geometry,
  MapFeature,
  RetractResult,
  StatusView,
  ZonePointsResponse,
} from '@shared/api/types';
import { agoIso, dateTime } from '@shared/lib/format';
import { onlineSignal } from '@shared/lib/online';
import { bboxParam, LngLat, MapGeometry, MapItem, MapView, TempPoint } from '@shared/map-adapter/map-adapter';
import { MapViewComponent } from '@shared/map-adapter/map-view';
import { Icon, normalizeIcon } from '@shared/ui/icon';
import { ToastService } from '@shared/ui/toast';
import { Disclaimer } from '@widgets/disclaimer/disclaimer';
import { MapFilters } from '@widgets/map-filters/map-filters';
import { ObjectCard } from '@widgets/object-card/object-card';

/** Масштаб запроса геозон для стартового вида: полигоны без лишней детализации. */
const COVERAGE_ZOOM = 10;

function toGeometry(geometry: Geometry): MapGeometry {
  if (geometry.type === 'Point') {
    return { type: 'Point', coordinates: [geometry.coordinates[0] ?? 0, geometry.coordinates[1] ?? 0] };
  }
  const rings: LngLat[][] = geometry.coordinates.map((ring) =>
    ring.map(([lon, lat]) => [lon ?? 0, lat ?? 0]),
  );
  return { type: 'Polygon', coordinates: rings };
}

/** Геометрия фиксации из ссылки ?object=<id>; null, пока данных с таким id нет. */
export function initialFocus(items: MapItem[], id: string | null): MapGeometry | null {
  if (!id) return null;
  return items.find((item) => item.id === id)?.geometry ?? null;
}

/** Геозона, для которой показывать временные точки: выбрана и это полигон. */
export function zoneIdForPoints(items: MapItem[], selectedId: string | null): string | null {
  if (!selectedId) return null;
  const item = items.find((candidate) => candidate.id === selectedId);
  return item?.geometry.type === 'Polygon' ? item.id : null;
}

/**
 * Точки на время загрузки: при обновлении той же зоны прежние остаются до ответа, при смене зоны
 * или снятии выбора — сразу убираются, чтобы не показывать чужие точки.
 */
export function pointsBeforeLoad(
  current: TempPoint[] | null,
  loadedZoneId: string | null,
  nextZoneId: string | null,
): TempPoint[] | null {
  return loadedZoneId === nextZoneId ? current : null;
}

/** Ответ API → временные точки карты. */
export function toTempPoints(response: ZonePointsResponse, now = Date.now()): TempPoint[] {
  return response.items.map((point) => ({
    id: point.address,
    lngLat: [point.location.lon, point.location.lat],
    tone: point.status.color,
    title: point.address,
    subtitle: `${point.status.name} · ${agoIso(point.observedAt, now)} · сообщений: ${point.observationCount}`,
    text: point.text ?? null,
  }));
}

/** Публичная карта: bbox-опрос, фильтры, карточка фиксации. */
@Component({
  selector: 'app-map-page',
  imports: [MapViewComponent, MapFilters, ObjectCard, Disclaimer, Icon],
  providers: [MapFeaturesStore],
  template: `
    <div class="mapview">
      <app-map-view class="canvas" [items]="items()" [selectedId]="selectedId()" [focus]="focus()"
        [fitGeometries]="coverage()" [userLocation]="true" [tempPoints]="tempPoints()"
        (selectItem)="select($event)" (viewChange)="onView($event)"
        (locationError)="onLocationError($event)" />

      <button class="btn btn--primary btn--sm filters-toggle" type="button" (click)="panelOpen.set(!panelOpen())">
        <app-icon name="filter" />{{ panelOpen() ? 'Скрыть фильтры' : 'Фильтры' }}
      </button>

      <div class="rail rail--left" [class.is-open]="panelOpen()">
        <section class="card stack" aria-labelledby="map-search-title">
          <h2 id="map-search-title" class="row"><app-icon name="pin" />Поиск фиксации</h2>
          <div class="field">
            <label for="map-search">Название</label>
            <input id="map-search" type="search" [value]="query()" (input)="query.set($any($event.target).value)"
              placeholder="Начните вводить название" autocomplete="off" />
          </div>
          @if (query().trim()) {
            <ul class="results">
              @for (item of matches(); track item.id) {
                <li>
                  <button class="btn btn--ghost btn--sm result" type="button" (click)="pick(item)">
                    <span class="grow">{{ item.name }}</span>
                    <small>{{ item.label }}</small>
                  </button>
                </li>
              } @empty {
                <li class="muted">Ничего не найдено</li>
              }
            </ul>
          }
          <button class="btn btn--ghost btn--sm about" type="button" (click)="disclaimerOpen.set(true)">
            <app-icon name="info" />О достоверности
          </button>
        </section>

        <app-map-filters [filters]="store.filters()" (filtersChange)="store.filters.set($event)"
          (groupsLoaded)="store.setKnownGroups($event)" (about)="disclaimerOpen.set(true)" />
      </div>

      @if (selectedId(); as id) {
        <div class="rail rail--right">
          <app-object-card [objectId]="id" [refreshKey]="refreshKey()" (close)="select(null)"
            (reported)="onReported($event)" (retracted)="onRetracted($event)" />
        </div>
      }

      @if (!online() || store.fromCache()) {
        <div class="plaque" role="status">
          Нет связи. Показаны данные от {{ store.generatedAt() ? dateTime(store.generatedAt()!) : 'последней загрузки' }}
        </div>
      } @else if (store.error(); as message) {
        <div class="plaque plaque--error" role="status">{{ message }}</div>
      }

      <app-disclaimer [(open)]="disclaimerOpen" />
    </div>`,
  styles: `
    :host { display: block; }
    .mapview { position: relative; height: calc(100dvh - var(--header-h)); min-height: 520px; }
    .canvas { position: absolute; inset: 0; }
    .rail { position: absolute; top: var(--gap); z-index: 4; width: min(340px, calc(100% - 2 * var(--gap)));
      max-height: calc(100% - 2 * var(--gap)); overflow-y: auto; overflow-x: hidden; overscroll-behavior: contain; }
    .rail--left { left: var(--gap); display: grid; gap: 8px; }
    .rail--right { right: var(--gap); }
    .results { list-style: none; margin: 0; padding: 0; display: grid; gap: 2px; }
    .result { width: 100%; justify-content: flex-start; gap: 8px; height: auto; min-height: 32px; }
    .result small { flex: none; }
    .about { justify-self: start; }
    .plaque { position: absolute; top: var(--gap); left: 50%; transform: translateX(-50%); z-index: 3;
      max-width: min(520px, calc(100% - 2 * var(--gap))); padding: 8px 14px; border-radius: 999px;
      background: var(--surface); border: 1px solid var(--st-limited); color: var(--ink); font-size: 13px;
      box-shadow: var(--shadow-float); }
    .plaque--error { border-color: var(--st-none); }
    .filters-toggle { display: none; }
    @media (max-width: 800px) {
      .filters-toggle { display: inline-flex; position: absolute; left: var(--gap); top: var(--gap); z-index: 6; }
      .plaque { top: calc(var(--gap) + var(--control-h) + 8px); }
      .rail--left { display: none; top: calc(var(--gap) + var(--control-h) + 8px); left: var(--gap); right: var(--gap); width: auto; }
      .rail--left.is-open { display: grid; }
      .rail--right { top: auto; bottom: var(--gap); left: var(--gap); right: var(--gap); width: auto; max-height: 62%; }
    }
  `,
})
export class MapPage {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(MapApi);
  private readonly toasts = inject(ToastService);
  protected readonly store = inject(MapFeaturesStore);
  protected readonly online = onlineSignal();
  protected readonly dateTime = dateTime;

  protected readonly selectedId = signal<string | null>(this.route.snapshot.queryParamMap.get('object'));
  protected readonly query = signal('');
  protected readonly focus = signal<MapGeometry | null>(null);
  /** Геозоны региона: карта стартует на них, а не в центре города по умолчанию. */
  protected readonly coverage = signal<readonly MapGeometry[] | null>(null);
  protected readonly panelOpen = signal(false);
  protected readonly disclaimerOpen = signal(false);
  protected readonly refreshKey = signal(0);
  /** Временные точки выбранной геозоны: только в памяти страницы, пока зона выбрана. */
  protected readonly tempPoints = signal<TempPoint[] | null>(null);
  private readonly pointsZoneId = computed(() => zoneIdForPoints(this.items(), this.selectedId()));
  private pointsSeq = 0;
  private pointsLoadedZoneId: string | null = null;

  private readonly initialObjectId = this.route.snapshot.queryParamMap.get('object');
  private focusedInitial = false;
  private cardTimer: ReturnType<typeof setTimeout> | null = null;

  protected readonly items = computed<MapItem[]>(() => this.store.items().map((feature) => this.toItem(feature)));

  protected readonly matches = computed<MapItem[]>(() => {
    const query = this.query().trim().toLowerCase();
    if (!query) return [];
    return this.items()
      .filter((item) => item.name.toLowerCase().includes(query))
      .slice(0, 8);
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      if (this.cardTimer) clearTimeout(this.cardTimer);
    });

    // Карточка перезагружается после каждого обновления карты и после отметки.
    effect(() => {
      if (this.store.generatedAt()) this.refreshKey.update((value) => value + 1);
    });

    // Ссылка ?object=<id>: один раз подлетаем к фиксации, как только она пришла с карты.
    effect(() => {
      if (this.focusedInitial || !this.initialObjectId) return;
      const geometry = initialFocus(this.items(), this.initialObjectId);
      if (!geometry) return;
      untracked(() => {
        this.focusedInitial = true;
        this.focus.set(geometry);
      });
    });

    // Выбранная фиксация живёт в адресе: ссылку ?object=<id> можно открыть напрямую.
    effect(() => {
      const object = this.selectedId();
      void this.router.navigate([], {
        relativeTo: this.route,
        queryParams: { object },
        replaceUrl: true,
      });
    });

    // Точки выбранной геозоны: при выборе и вместе с обновлением карты; снятие выбора их убирает.
    effect(() => {
      const zoneId = this.pointsZoneId();
      this.refreshKey();
      untracked(() => void this.loadPoints(zoneId));
    });

    // Прямая ссылка на фиксацию важнее стартового вида: не перебиваем её обзором геозон.
    if (!this.initialObjectId) void this.loadCoverage();
  }

  protected onView(view: MapView): void {
    this.store.view.set(view);
  }

  protected select(id: string | null, fly = false): void {
    this.selectedId.set(id);
    this.panelOpen.set(false);
    if (!id || !fly) return;
    const geometry = this.items().find((item) => item.id === id)?.geometry ?? null;
    this.focus.set(geometry);
  }

  protected pick(item: MapItem): void {
    this.select(item.id, true);
    this.query.set('');
  }

  protected onLocationError(message: string): void {
    this.toasts.error(message);
  }

  protected onReported(response: CreateObservationResponse): void {
    this.applyStatus(response.status);
    this.store.refreshSoon();
    this.reloadCardSoon();
  }

  protected onRetracted(result: RetractResult): void {
    this.applyStatus(result.status);
    this.store.refreshSoon();
    this.reloadCardSoon();
  }

  private applyStatus(status: StatusView | null): void {
    const id = this.selectedId();
    if (id) this.store.applyStatus(id, status);
  }

  /** Карточку перезагружаем после фонового пересчёта проекции, иначе придёт старый статус. */
  private reloadCardSoon(): void {
    if (this.cardTimer) clearTimeout(this.cardTimer);
    this.cardTimer = setTimeout(() => {
      this.cardTimer = null;
      this.refreshKey.update((value) => value + 1);
    }, 1500);
  }

  /**
   * Один лёгкий запрос по региону: только полигоны. По ним карта подбирает стартовый
   * вид, чтобы работа начиналась возле геозон. Ошибку не показываем — остаётся
   * вид по умолчанию из environment, а данные загрузятся обычным bbox-опросом.
   */
  private async loadCoverage(): Promise<void> {
    const [minLon, minLat, maxLon, maxLat] = environment.mapCoverageBbox;
    try {
      const response = await this.api.listFeatures({
        bbox: bboxParam({ minLon, minLat, maxLon, maxLat }),
        zoom: COVERAGE_ZOOM,
        kinds: ['polygon'],
      });
      if (response.notModified) return;
      // Пока ждали ответ, пользователь мог выбрать фиксацию — его вид не перебиваем.
      if (this.selectedId() || this.focus()) return;
      const geometries = response.body.items
        .filter((feature) => feature.geometry.type === 'Polygon')
        .map((feature) => toGeometry(feature.geometry));
      if (geometries.length) this.coverage.set(geometries);
    } catch {
      // Стартовый вид по умолчанию — рабочая альтернатива, отдельного сообщения не нужно.
    }
  }

  private async loadPoints(zoneId: string | null): Promise<void> {
    const seq = ++this.pointsSeq;
    this.tempPoints.set(pointsBeforeLoad(this.tempPoints(), this.pointsLoadedZoneId, zoneId));
    this.pointsLoadedZoneId = zoneId;
    if (!zoneId) return;
    try {
      const response = await this.api.getPoints(zoneId);
      if (seq === this.pointsSeq) this.tempPoints.set(toTempPoints(response));
    } catch (err) {
      // Точки — дополнение к статусу зоны: при ошибке обновления оставляем прежние точки этой же
      // зоны (чужие уже убраны), без тоста на каждый опрос.
      console.warn('Карта: не удалось загрузить точки зоны', err);
    }
  }

  private toItem(feature: MapFeature): MapItem {
    return {
      id: feature.id,
      name: feature.name,
      geometry: toGeometry(feature.geometry),
      tone: feature.status?.color ?? 'stale',
      icon: normalizeIcon(feature.status?.icon),
      label: feature.status?.name ?? 'Неизвестно',
      stale: !feature.status || feature.status.freshness !== 'fresh',
    };
  }
}
