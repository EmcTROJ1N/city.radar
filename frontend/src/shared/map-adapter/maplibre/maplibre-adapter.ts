import { AttributionControl, Map as MapLibreMap, Marker, NavigationControl, Popup } from 'maplibre-gl';
import type {
  ExpressionSpecification,
  FillLayerSpecification,
  GeoJSONSource,
  LineLayerSpecification,
  StyleSpecification,
} from 'maplibre-gl';
import type { Feature, FeatureCollection, LineString, Point, Polygon } from 'geojson';
import { Tone } from '../../lib/tone';
import { IconName, iconPaths } from '../../ui/icon';
import {
  DrawMode,
  BBox,
  LngLat,
  MapAdapter,
  MapGeometry,
  MapItem,
  MapStyleUrls,
  MapView,
  TempPoint,
} from '../map-adapter';
import { bboxOf, bboxOfMany, centroid, closeRing, isValidGeometry, isValidPolygon } from '../geometry';
import { geolocationErrorMessage, userLocationFrom } from '../geolocation';

const SOURCE_ID = 'zones';
const FILL_LAYER = 'zones-fill';
const LINE_LAYER = 'zones-line';
const STALE_LINE_LAYER = 'zones-line-stale';
const VIEW_DEBOUNCE_MS = 300;
const ZONE_LABEL_MIN_ZOOM = 12;

const DRAFT_SOURCE = 'draft';
const DRAFT_LINE_LAYER = 'draft-line';
const DRAFT_VERTEX_LAYER = 'draft-vertices';
/** Ближе этого по экрану клик считается повторным или попаданием в первую вершину. */
const SAME_VERTEX_PX = 5;
const CLOSE_VERTEX_PX = 10;

const TONES: readonly Tone[] = ['ok', 'limited', 'none', 'queue', 'schedule', 'stale'];

function cssVar(name: string, fallback: string): string {
  if (typeof document === 'undefined') return fallback;
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return value || fallback;
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function iconSvg(name: IconName): string {
  const paths = iconPaths(name).map((d) => `<path d="${d}" />`).join('');
  return `<svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">${paths}</svg>`;
}

function boundsOf(geometry: MapGeometry): [LngLat, LngLat] {
  const box = bboxOf(geometry);
  return [
    [box.minLon, box.minLat],
    [box.maxLon, box.maxLat],
  ];
}

/**
 * Полигоны в GeoJSON. Строковый id кладём в properties: источник создаётся
 * с promoteId: 'id', иначе MapLibre теряет нечисловые id фич.
 */
export function zoneFeatureCollection(items: MapItem[]): FeatureCollection {
  const features: Feature[] = [];
  for (const item of items) {
    if (item.geometry.type !== 'Polygon') continue;
    features.push({
      type: 'Feature',
      properties: {
        id: item.id,
        name: item.name,
        tone: item.tone,
        label: item.label,
        stale: item.stale,
      },
      geometry: item.geometry,
    });
  }
  return { type: 'FeatureCollection', features };
}

/**
 * Наши классы на HTML-маркерах карты. Остальные классы (в первую очередь
 * служебный `maplibregl-marker`, задающий `position: absolute`) трогать нельзя.
 */
const OWN_MARKER_CLASSES = ['map-pin', 'map-zone-label'];

/**
 * Обновляет классы состояния маркера, не затирая служебные классы MapLibre.
 * Через `className` их терять нельзя: без `maplibregl-marker` подпись зоны
 * становится блочным элементом на всю ширину карты.
 */
export function applyMarkerClasses(element: HTMLElement, classes: string[]): void {
  for (const name of [...element.classList]) {
    if (OWN_MARKER_CLASSES.includes(name) || name.startsWith('tone-') || name.startsWith('is-')) {
      element.classList.remove(name);
    }
  }
  element.classList.add(...classes);
}

/** Маркер временной точки: кнопка цвета статуса; подробности — в подсказке по клику. */
export function tempPointElement(point: TempPoint): HTMLButtonElement {
  const element = document.createElement('button');
  element.type = 'button';
  element.className = `map-temp-point tone-${point.tone}`;
  element.setAttribute('aria-label', `${point.title}: ${point.subtitle}`);
  element.title = point.title;
  return element;
}

/** Содержимое подсказки временной точки. Только textContent: текст сообщения — данные из чата. */
export function tempPointPopupContent(point: TempPoint): HTMLElement {
  const root = document.createElement('div');
  root.className = 'map-temp-popup';

  const title = document.createElement('strong');
  title.textContent = point.title;
  const subtitle = document.createElement('small');
  subtitle.className = `tone-${point.tone}`;
  subtitle.textContent = point.subtitle;
  root.append(title, subtitle);

  if (point.text) {
    const quote = document.createElement('blockquote');
    quote.textContent = point.text;
    root.append(quote);
  }

  return root;
}

/**
 * id слоя подложки, перед которым вставляем свои слои: первая подпись, идущая
 * после последнего «земляного» слоя (заливки, линии, фон).
 *
 * Искать просто первую подпись нельзя: в наших стилях OpenFreeMap первой идёт
 * `water_name` — она стоит ПЕРЕД зданиями и дорогами. Вставка перед ней кладёт
 * очертания геозон под здания, и они перечёркиваются. Нужен слой выше всей
 * подложки, но ниже её подписей.
 */
export function overlayBeforeLayerId(
  layers: readonly { id: string; type: string }[],
): string | undefined {
  let lastGround = -1;
  layers.forEach((layer, index) => {
    if (layer.type !== 'symbol') lastGround = index;
  });
  const label = layers[lastGround + 1];
  return label?.type === 'symbol' ? label.id : undefined;
}

/** Реализация карты на MapLibre GL JS. Подложка — только свои файлы стиля из `public/map-style`. */
export class MapLibreAdapter implements MapAdapter {
  private map: MapLibreMap | null = null;
  private styleUrls: MapStyleUrls = { light: '', dark: '' };
  private appliedStyleUrl: string | null = null;
  private themeObserver: MutationObserver | null = null;
  private items: MapItem[] = [];
  private selectedId: string | null = null;
  private colors: Record<Tone, string> = {
    ok: '#1673c2',
    limited: '#c2570c',
    none: '#b9421a',
    queue: '#6b4fa3',
    schedule: '#6b4fa3',
    stale: '#7c8c92',
  };
  private selectCb: ((id: string) => void) | null = null;
  private viewCb: ((view: MapView) => void) | null = null;
  private drawCb: ((geometry: MapGeometry | null) => void) | null = null;
  private viewTimer: ReturnType<typeof setTimeout> | null = null;
  private readonly pins = new Map<string, { marker: Marker; element: HTMLButtonElement }>();
  private readonly labels = new Map<string, { marker: Marker; element: HTMLDivElement }>();
  private tempMarkers: Marker[] = [];
  private tempPopup: Popup | null = null;
  private readonly warnedInvalid = new Set<string>();
  private locationErrorCb: ((message: string) => void) | null = null;
  private userMarker: Marker | null = null;
  private userPosition: LngLat | null = null;
  private geolocationWatch: number | null = null;
  private recenterOnFix = false;

  private drawMode: DrawMode = 'none';
  private drawPoint: LngLat | null = null;
  private vertices: LngLat[] = [];
  private polygonClosed = false;
  private draftMarker: Marker | null = null;
  private vertexMarkers: Marker[] = [];
  private doubleClickZoomWasEnabled = false;

  async mount(
    container: HTMLElement,
    opts: { center: LngLat; zoom: number; styleUrls: MapStyleUrls },
  ): Promise<void> {
    this.colors = {
      ok: cssVar('--st-ok', this.colors.ok),
      limited: cssVar('--st-limited', this.colors.limited),
      none: cssVar('--st-none', this.colors.none),
      queue: cssVar('--st-queue', this.colors.queue),
      schedule: cssVar('--st-schedule', this.colors.schedule),
      stale: cssVar('--st-stale', this.colors.stale),
    };

    this.styleUrls = opts.styleUrls;
    const styleUrl = this.currentStyleUrl();
    this.appliedStyleUrl = styleUrl || null;

    const map = new MapLibreMap({
      container,
      style: styleUrl || this.emptyStyle(),
      center: opts.center,
      zoom: opts.zoom,
      attributionControl: false,
    });
    this.map = map;

    // Ссылка на карту уже в атрибуции векторного источника — дублировать её не нужно.
    map.addControl(
      new AttributionControl(
        styleUrl ? { compact: true } : { customAttribution: '© участники OpenStreetMap', compact: true },
      ),
    );
    map.addControl(new NavigationControl({ showCompass: false }), 'top-right');

    // Ошибка загрузки тайлов/стиля не должна ронять страницу (: своих тайлов может не быть).
    map.on('error', (event) => console.warn('Карта: ошибка загрузки данных', event.error ?? event));

    this.bindZoneEvents(map);
    map.on('click', (event) => this.onDrawClick([event.lngLat.lng, event.lngLat.lat]));
    map.on('dblclick', () => this.onDrawDoubleClick());
    map.on('moveend', () => this.scheduleView());
    map.on('zoom', () => this.syncLabelVisibility());
    // Новый стиль (в том числе после смены темы) удаляет слои зон — возвращаем их.
    map.on('style.load', () => this.restoreOverlays());
    this.observeTheme();

    await this.whenReady();
    this.restoreOverlays();

    if (this.drawMode !== 'none') {
      this.ensureDraftLayers();
      this.syncDrawMode();
      this.refreshDraft();
    }
    this.emitView();
  }

  destroy(): void {
    if (this.viewTimer) clearTimeout(this.viewTimer);
    this.viewTimer = null;
    this.stopGeolocation();
    this.themeObserver?.disconnect();
    this.themeObserver = null;
    this.appliedStyleUrl = null;
    for (const { marker } of this.pins.values()) marker.remove();
    for (const { marker } of this.labels.values()) marker.remove();
    this.setTempPoints(null);
    this.removeDraftMarker();
    this.removeVertexMarkers();
    this.pins.clear();
    this.labels.clear();
    this.map?.remove();
    this.map = null;
  }

  setItems(items: MapItem[]): void {
    this.items = items.filter((item) => this.acceptItem(item));
    this.updateZones();
    this.syncPins();
    this.syncLabels();
  }

  /** Некорректная геометрия роняет отрисовку всей карты — такие элементы пропускаем. */
  private acceptItem(item: MapItem): boolean {
    if (isValidGeometry(item.geometry)) return true;
    if (!this.warnedInvalid.has(item.id)) {
      this.warnedInvalid.add(item.id);
      console.warn(`Карта: фиксация «${item.name}» (${item.id}) пропущена — некорректные координаты`);
    }
    return false;
  }

  setTempPoints(points: readonly TempPoint[] | null): void {
    this.tempPopup?.remove();
    this.tempPopup = null;
    for (const marker of this.tempMarkers) marker.remove();
    this.tempMarkers = [];

    const map = this.map;
    if (!map || !points?.length) return;

    // HTML-маркеры переживают смену стиля (тему) — пересоздавать при style.load не нужно.
    for (const point of points) {
      const element = tempPointElement(point);
      const marker = new Marker({ element }).setLngLat(point.lngLat).addTo(map);
      element.addEventListener('click', (event) => {
        event.stopPropagation();
        this.tempPopup?.remove();
        this.tempPopup = new Popup({ offset: 12, maxWidth: '280px' })
          .setLngLat(point.lngLat)
          .setDOMContent(tempPointPopupContent(point))
          .addTo(map);
      });
      this.tempMarkers.push(marker);
    }
  }

  setSelected(id: string | null): void {
    this.selectedId = id;
    if (this.map?.getSource(SOURCE_ID)) {
      for (const item of this.items) {
        if (item.geometry.type !== 'Polygon') continue;
        this.map.setFeatureState({ source: SOURCE_ID, id: item.id }, { selected: item.id === id });
      }
    }
    for (const [itemId, pin] of this.pins) {
      pin.element.classList.toggle('is-selected', itemId === id);
      pin.element.setAttribute('aria-current', itemId === id ? 'true' : 'false');
    }
    for (const [itemId, label] of this.labels) {
      label.element.classList.toggle('is-selected', itemId === id);
    }
  }

  flyTo(geometry: MapGeometry): void {
    const map = this.map;
    if (!map) return;
    if (geometry.type === 'Point') {
      map.flyTo({ center: geometry.coordinates, zoom: Math.max(map.getZoom(), 14), duration: 600 });
      return;
    }
    map.fitBounds(boundsOf(geometry), { padding: 64, duration: 600, maxZoom: 15 });
  }

  /** Стартовый вид по всем геозонам: пользователь сразу видит свою рабочую область. */
  fitGeometries(geometries: readonly MapGeometry[]): void {
    const map = this.map;
    if (!map) return;
    const box = bboxOfMany(geometries);
    if (!box) return;
    map.fitBounds(
      [
        [box.minLon, box.minLat],
        [box.maxLon, box.maxLat],
      ],
      { padding: 64, duration: 0, maxZoom: 15 },
    );
  }

  startGeolocation(): void {
    if (this.geolocationWatch !== null) return;
    if (typeof navigator === 'undefined' || !navigator.geolocation) {
      this.reportLocationError('Браузер не поддерживает геолокацию.');
      return;
    }
    if (typeof window !== 'undefined' && window.isSecureContext === false) {
      this.reportLocationError('Геолокация доступна только по защищённому соединению (HTTPS).');
      return;
    }

    this.geolocationWatch = navigator.geolocation.watchPosition(
      (position) => this.applyUserPosition(position),
      (error) => {
        this.reportLocationError(geolocationErrorMessage(error));
        if (error.code === 1) this.stopGeolocation();
      },
      { enableHighAccuracy: true, maximumAge: 10_000, timeout: 20_000 },
    );
  }

  flyToUser(): void {
    if (this.userPosition) {
      this.centerOnUser();
      return;
    }
    this.recenterOnFix = true;
    this.startGeolocation();
  }

  stopGeolocation(): void {
    if (this.geolocationWatch !== null) {
      if (typeof navigator !== 'undefined' && navigator.geolocation) {
        navigator.geolocation.clearWatch(this.geolocationWatch);
      }
      this.geolocationWatch = null;
    }
    this.recenterOnFix = false;
    this.userMarker?.remove();
    this.userMarker = null;
    this.userPosition = null;
  }

  onLocationError(cb: (message: string) => void): void {
    this.locationErrorCb = cb;
  }

  private applyUserPosition(position: GeolocationPosition): void {
    const location = userLocationFrom(position);
    if (!location) return;
    this.userPosition = location.coordinates;
    this.syncUserMarker(location.accuracy);
    if (this.recenterOnFix) {
      this.recenterOnFix = false;
      this.centerOnUser();
    }
  }

  private reportLocationError(message: string): void {
    this.recenterOnFix = false;
    this.locationErrorCb?.(message);
  }

  private centerOnUser(): void {
    const map = this.map;
    if (!map || !this.userPosition) return;
    map.flyTo({ center: this.userPosition, zoom: Math.max(map.getZoom(), 16), duration: 600 });
  }

  private syncUserMarker(accuracy: number): void {
    const map = this.map;
    if (!map || !this.userPosition) return;

    if (!this.userMarker) {
      const element = document.createElement('div');
      element.className = 'map-user-location';
      element.innerHTML =
        '<span class="map-user-location__pulse"></span><span class="map-user-location__dot"></span>';
      this.userMarker = new Marker({ element, anchor: 'center' })
        .setLngLat(this.userPosition)
        .addTo(map);
    } else {
      this.userMarker.setLngLat(this.userPosition);
    }

    const element = this.userMarker.getElement();
    const label =
      accuracy > 0
        ? `Ваше местоположение, точность ±${Math.round(accuracy)} м`
        : 'Ваше местоположение';
    element.setAttribute('role', 'img');
    element.setAttribute('aria-label', label);
    element.title = label;
  }

  onSelect(cb: (id: string) => void): void {
    this.selectCb = cb;
  }

  onViewChange(cb: (view: MapView) => void): void {
    this.viewCb = cb;
  }

  setDraw(mode: DrawMode, initial: MapGeometry | null = null): void {
    this.drawMode = mode;
    this.clearDraftState();
    if (initial && mode === 'point' && initial.type === 'Point') {
      this.drawPoint = [initial.coordinates[0], initial.coordinates[1]];
    }
    if (initial && mode === 'polygon' && initial.type === 'Polygon') {
      const ring = initial.coordinates[0] ?? [];
      this.vertices = ring.map((point): LngLat => [point[0], point[1]]);
      if (this.vertices.length > 1) {
        const first = this.vertices[0]!;
        const last = this.vertices[this.vertices.length - 1]!;
        if (first[0] === last[0] && first[1] === last[1]) this.vertices.pop();
      }
      this.polygonClosed = isValidPolygon(this.vertices);
    }

    const map = this.map;
    if (!map) return;
    if (mode !== 'none') this.ensureDraftLayers();
    this.syncDrawMode();
    this.refreshDraft();
  }

  undoVertex(): void {
    if (this.drawMode !== 'polygon') return;
    this.vertices.pop();
    this.polygonClosed = this.polygonClosed && isValidPolygon(this.vertices);
    this.refreshDraft();
  }

  clearDraft(): void {
    this.clearDraftState();
    this.refreshDraft();
  }

  onDrawChange(cb: (geometry: MapGeometry | null) => void): void {
    this.drawCb = cb;
  }

  private emptyStyle(): StyleSpecification {
    return {
      version: 8,
      sources: {},
      layers: [
        {
          id: 'background',
          type: 'background',
          paint: { 'background-color': cssVar('--map-land', '#e3ece9') },
        },
      ],
    };
  }

  /** Тема ставится атрибутом data-theme на <html> (shared/config/apply-ui-config.ts). */
  private currentStyleUrl(): string {
    const theme = typeof document === 'undefined' ? '' : document.documentElement.dataset['theme'];
    return theme === 'dark' ? this.styleUrls.dark : this.styleUrls.light;
  }

  private observeTheme(): void {
    if (typeof MutationObserver === 'undefined' || typeof document === 'undefined') return;
    this.themeObserver = new MutationObserver(() => this.applyThemeStyle());
    this.themeObserver.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ['data-theme'],
    });
  }

  private applyThemeStyle(): void {
    const map = this.map;
    if (!map) return;
    const url = this.currentStyleUrl();
    if (!url || url === this.appliedStyleUrl) return;
    this.appliedStyleUrl = url;
    map.setStyle(url);
  }

  /**
   * Возвращает собственные слои поверх подложки: setStyle удаляет их.
   * HTML-маркеры точек и подписей зон переживают смену стиля.
   */
  private restoreOverlays(): void {
    try {
      this.ensureLayers();
      this.updateZones();
      if (this.drawMode !== 'none') this.ensureDraftLayers();
    } catch (err) {
      console.warn('Карта: не удалось восстановить слои', err);
    }
  }

  /** Точка вставки своих слоёв: над подложкой, под её подписями. */
  private overlayBeforeLayer(): string | undefined {
    return overlayBeforeLayerId(this.map?.getStyle()?.layers ?? []);
  }

  private bindZoneEvents(map: MapLibreMap): void {
    map.on('click', FILL_LAYER, (event) => {
      if (this.drawMode !== 'none') return;
      const id = event.features?.[0]?.properties?.['id'];
      if (typeof id === 'string') this.selectCb?.(id);
    });
    map.on('mousemove', FILL_LAYER, () => {
      if (this.drawMode !== 'none') return;
      map.getCanvas().style.cursor = 'pointer';
    });
    map.on('mouseleave', FILL_LAYER, () => {
      if (this.drawMode !== 'none') return;
      map.getCanvas().style.cursor = '';
    });
  }

  private whenReady(): Promise<void> {
    const map = this.map;
    if (!map) return Promise.resolve();
    return new Promise<void>((resolve) => {
      let done = false;
      const finish = () => {
        if (done) return;
        done = true;
        clearTimeout(timer);
        resolve();
      };
      const timer = setTimeout(finish, 5000);
      map.once('load', finish);
      map.once('error', finish);
    });
  }

  private colorExpression(): ExpressionSpecification {
    const expression: unknown[] = ['match', ['get', 'tone']];
    for (const tone of TONES) expression.push(tone, this.colors[tone]);
    expression.push(this.colors.stale);
    return expression as unknown as ExpressionSpecification;
  }

  private ensureLayers(): void {
    const map = this.map;
    if (!map || map.getSource(SOURCE_ID)) return;

    map.addSource(SOURCE_ID, { type: 'geojson', data: this.zoneData(), promoteId: 'id' });

    const before = this.overlayBeforeLayer();
    const width: ExpressionSpecification = [
      'case',
      ['boolean', ['feature-state', 'selected'], false],
      4,
      2,
    ];
    const fill: FillLayerSpecification = {
      id: FILL_LAYER,
      type: 'fill',
      source: SOURCE_ID,
      paint: {
        'fill-color': this.colorExpression(),
        'fill-opacity': ['case', ['get', 'stale'], 0.12, 0.35],
      },
    };
    const line: LineLayerSpecification = {
      id: LINE_LAYER,
      type: 'line',
      source: SOURCE_ID,
      filter: ['!=', ['get', 'stale'], true],
      paint: {
        'line-color': this.colorExpression(),
        'line-width': width,
      },
    };
    const staleLine: LineLayerSpecification = {
      id: STALE_LINE_LAYER,
      type: 'line',
      source: SOURCE_ID,
      filter: ['==', ['get', 'stale'], true],
      paint: {
        'line-color': this.colorExpression(),
        'line-width': 2,
        'line-dasharray': [2, 2],
      },
    };
    map.addLayer(fill, before);
    map.addLayer(line, before);
    map.addLayer(staleLine, before);

    this.raiseDraftLayers();
  }

  private zoneData(): FeatureCollection {
    return zoneFeatureCollection(this.items);
  }

  private updateZones(): void {
    const source = this.map?.getSource(SOURCE_ID) as GeoJSONSource | undefined;
    if (!source) return;
    source.setData(this.zoneData());
    for (const item of this.items) {
      if (item.geometry.type !== 'Polygon') continue;
      this.map?.setFeatureState(
        { source: SOURCE_ID, id: item.id },
        { selected: item.id === this.selectedId },
      );
    }
  }

  private syncPins(): void {
    const map = this.map;
    if (!map) return;

    const points = this.items.filter((item) => item.geometry.type === 'Point');
    const alive = new Set(points.map((item) => item.id));

    for (const [id, pin] of this.pins) {
      if (alive.has(id)) continue;
      pin.marker.remove();
      this.pins.delete(id);
    }

    for (const item of points) {
      if (item.geometry.type !== 'Point') continue;
      const position = item.geometry.coordinates;
      try {
        let pin = this.pins.get(item.id);
        if (!pin) {
          const element = document.createElement('button');
          element.type = 'button';
          element.addEventListener('click', (event) => {
            event.stopPropagation();
            if (this.drawMode !== 'none') return;
            this.selectCb?.(item.id);
          });
          const marker = new Marker({ element, anchor: 'center' }).setLngLat(position).addTo(map);
          pin = { marker, element };
          this.pins.set(item.id, pin);
        } else {
          pin.marker.setLngLat(position);
        }
        this.paintPin(pin.element, item);
      } catch (err) {
        console.warn(`Карта: не удалось отрисовать маркер «${item.name}» (${item.id})`, err);
      }
    }
  }

  /**
   * Наши классы состояния меняем только через classList: MapLibre вешает на элемент
   * `maplibregl-marker` (position: absolute) — через `className` он теряется, и подпись
   * зоны растягивается на всю ширину карты.
   */
  private paintPin(element: HTMLButtonElement, item: MapItem): void {
    const classes = ['map-pin', `tone-${item.tone}`];
    if (item.stale) classes.push('is-stale');
    if (item.id === this.selectedId) classes.push('is-selected');
    applyMarkerClasses(element, classes);
    element.setAttribute('aria-label', `${item.name}: ${item.label}`);
    element.setAttribute('aria-current', item.id === this.selectedId ? 'true' : 'false');
    // Название — в приоритете, статус — подпись под ним (как у подписи геозоны).
    element.innerHTML =
      `<span class="map-pin__icon">${iconSvg(item.icon)}</span>` +
      `<span class="map-pin__text">` +
      `<span class="map-pin__name">${escapeHtml(item.name)}</span>` +
      `<span class="map-pin__state">${escapeHtml(item.label)}</span>` +
      `</span>`;
  }

  private syncLabels(): void {
    const map = this.map;
    if (!map) return;

    const zones = this.items.filter((item) => item.geometry.type === 'Polygon');
    const alive = new Set(zones.map((item) => item.id));

    for (const [id, label] of this.labels) {
      if (alive.has(id)) continue;
      label.marker.remove();
      this.labels.delete(id);
    }

    for (const item of zones) {
      const position = centroid(item.geometry);
      let label = this.labels.get(item.id);
      if (!label) {
        const element = document.createElement('div');
        element.className = 'map-zone-label';
        const marker = new Marker({ element, anchor: 'center' }).setLngLat(position).addTo(map);
        label = { marker, element };
        this.labels.set(item.id, label);
      } else {
        label.marker.setLngLat(position);
      }
      const classes = ['map-zone-label', `tone-${item.tone}`];
      if (item.id === this.selectedId) classes.push('is-selected');
      if ((map.getZoom() ?? 0) < ZONE_LABEL_MIN_ZOOM) classes.push('is-hidden');
      applyMarkerClasses(label.element, classes);
      label.element.innerHTML = `<span class="map-zone-label__name">${escapeHtml(item.name)}</span><span class="map-zone-label__state">${escapeHtml(item.label)}</span>`;
    }
  }

  private syncLabelVisibility(): void {
    const zoom = this.map?.getZoom() ?? 0;
    for (const { element } of this.labels.values()) {
      element.classList.toggle('is-hidden', zoom < ZONE_LABEL_MIN_ZOOM);
    }
  }

  private clearDraftState(): void {
    this.drawPoint = null;
    this.vertices = [];
    this.polygonClosed = false;
    this.removeDraftMarker();
    this.removeVertexMarkers();
  }

  private syncDrawMode(): void {
    const map = this.map;
    if (!map) return;
    const drawing = this.drawMode !== 'none';
    map.getCanvas().style.cursor = drawing ? 'crosshair' : '';
    if (drawing && map.doubleClickZoom.isEnabled()) {
      map.doubleClickZoom.disable();
      this.doubleClickZoomWasEnabled = true;
    } else if (!drawing && this.doubleClickZoomWasEnabled) {
      map.doubleClickZoom.enable();
      this.doubleClickZoomWasEnabled = false;
    }
  }

  private onDrawClick(point: LngLat): void {
    if (!this.map || this.drawMode === 'none') return;
    if (this.drawMode === 'point') {
      this.drawPoint = [point[0], point[1]];
      this.refreshDraft();
      return;
    }
    if (this.polygonClosed) return;

    const first = this.vertices[0];
    if (first && this.vertices.length >= 3 && this.pixelDistance(first, point) <= CLOSE_VERTEX_PX) {
      this.closePolygon();
      return;
    }
    const last = this.vertices[this.vertices.length - 1];
    if (last && this.pixelDistance(last, point) <= SAME_VERTEX_PX) return;

    this.vertices.push([point[0], point[1]]);
    this.refreshDraft();
  }

  private onDrawDoubleClick(): void {
    if (this.drawMode !== 'polygon' || this.polygonClosed) return;
    this.closePolygon();
  }

  private closePolygon(): void {
    if (!isValidPolygon(this.vertices)) return;
    this.polygonClosed = true;
    this.refreshDraft();
  }

  private draftGeometry(): MapGeometry | null {
    if (this.drawMode === 'point') {
      return this.drawPoint
        ? { type: 'Point', coordinates: [this.drawPoint[0], this.drawPoint[1]] }
        : null;
    }
    if (this.drawMode === 'polygon' && this.polygonClosed && isValidPolygon(this.vertices)) {
      return { type: 'Polygon', coordinates: [closeRing(this.vertices)] };
    }
    return null;
  }

  private draftData(): FeatureCollection {
    const features: Feature[] = [];
    if (this.drawMode !== 'polygon' || this.vertices.length === 0) {
      return { type: 'FeatureCollection', features };
    }

    if (this.polygonClosed && isValidPolygon(this.vertices)) {
      const polygon: Polygon = { type: 'Polygon', coordinates: [closeRing(this.vertices)] };
      features.push({ type: 'Feature', properties: { closed: true }, geometry: polygon });
    } else if (this.vertices.length >= 2) {
      const line: LineString = { type: 'LineString', coordinates: this.vertices };
      features.push({ type: 'Feature', properties: {}, geometry: line });
    }

    for (const [lon, lat] of this.vertices) {
      const point: Point = { type: 'Point', coordinates: [lon, lat] };
      features.push({ type: 'Feature', properties: {}, geometry: point });
    }
    return { type: 'FeatureCollection', features };
  }

  private ensureDraftLayers(): void {
    const map = this.map;
    if (!map || map.getSource(DRAFT_SOURCE)) return;

    map.addSource(DRAFT_SOURCE, { type: 'geojson', data: this.draftData() });
    map.addLayer({
      id: DRAFT_LINE_LAYER,
      type: 'line',
      source: DRAFT_SOURCE,
      filter: ['any', ['==', ['geometry-type'], 'LineString'], ['==', ['geometry-type'], 'Polygon']],
      paint: {
        'line-color': cssVar('--brand', '#0e4a5a'),
        'line-width': 2.5,
        'line-dasharray': [2, 1.5],
      },
    });
    map.addLayer({
      id: DRAFT_VERTEX_LAYER,
      type: 'circle',
      source: DRAFT_SOURCE,
      filter: ['==', ['geometry-type'], 'Point'],
      paint: {
        'circle-radius': 5,
        'circle-color': cssVar('--brand', '#0e4a5a'),
        'circle-stroke-color': cssVar('--surface', '#fff'),
        'circle-stroke-width': 2,
      },
    });
    this.raiseDraftLayers();
  }

  private raiseDraftLayers(): void {
    const map = this.map;
    if (!map) return;
    for (const id of [DRAFT_LINE_LAYER, DRAFT_VERTEX_LAYER]) {
      if (map.getLayer(id)) map.moveLayer(id);
    }
  }

  private refreshDraft(): void {
    this.syncPointMarker();
    this.syncVertexMarkers();
    this.refreshDraftData();
  }

  private refreshDraftData(): void {
    const source = this.map?.getSource(DRAFT_SOURCE) as GeoJSONSource | undefined;
    source?.setData(this.draftData());
    this.drawCb?.(this.draftGeometry());
  }

  private syncPointMarker(): void {
    if (this.drawMode !== 'point' || !this.drawPoint || !this.map) {
      this.removeDraftMarker();
      return;
    }
    if (!this.draftMarker) {
      const element = document.createElement('div');
      element.className = 'draft-point';
      element.style.cssText =
        'width:18px;height:18px;border-radius:50%;background:var(--brand);' +
        'border:3px solid var(--surface);box-shadow:0 0 0 2px color-mix(in srgb,var(--brand) 45%,transparent);' +
        'cursor:grab;';
      const marker = new Marker({ element, anchor: 'center', draggable: true })
        .setLngLat(this.drawPoint)
        .addTo(this.map);
      marker.on('drag', () => {
        const position = marker.getLngLat();
        this.drawPoint = [position.lng, position.lat];
        this.refreshDraftData();
      });
      this.draftMarker = marker;
      return;
    }
    this.draftMarker.setLngLat(this.drawPoint);
  }

  private removeDraftMarker(): void {
    this.draftMarker?.remove();
    this.draftMarker = null;
  }

  private syncVertexMarkers(): void {
    const map = this.map;
    const needed = map && this.drawMode === 'polygon' && this.polygonClosed ? this.vertices.length : 0;
    while (this.vertexMarkers.length > needed) {
      this.vertexMarkers.pop()?.remove();
    }
    for (let index = 0; index < needed; index++) {
      let marker = this.vertexMarkers[index];
      if (!marker) {
        marker = this.createVertexMarker(index);
        this.vertexMarkers[index] = marker;
      }
      marker.setLngLat(this.vertices[index]!);
    }
  }

  private createVertexMarker(index: number): Marker {
    const map = this.map!;
    const element = document.createElement('div');
    element.style.cssText = 'width:18px;height:18px;border-radius:50%;background:transparent;cursor:grab;';
    const marker = new Marker({ element, anchor: 'center', draggable: true })
      .setLngLat(this.vertices[index]!)
      .addTo(map);
    marker.on('dragstart', () => map.dragPan.disable());
    marker.on('drag', () => {
      const position = marker.getLngLat();
      this.vertices[index] = [position.lng, position.lat];
      this.refreshDraftData();
    });
    marker.on('dragend', () => map.dragPan.enable());
    return marker;
  }

  private removeVertexMarkers(): void {
    for (const marker of this.vertexMarkers) marker.remove();
    this.vertexMarkers = [];
  }

  private pixelDistance(a: LngLat, b: LngLat): number {
    const map = this.map;
    if (!map) return Infinity;
    const pa = map.project(a);
    const pb = map.project(b);
    return Math.hypot(pa.x - pb.x, pa.y - pb.y);
  }

  private scheduleView(): void {
    if (this.viewTimer) clearTimeout(this.viewTimer);
    this.viewTimer = setTimeout(() => {
      this.viewTimer = null;
      this.emitView();
    }, VIEW_DEBOUNCE_MS);
  }

  private emitView(): void {
    const map = this.map;
    if (!map || !this.viewCb) return;
    const bounds = map.getBounds();
    const bbox: BBox = {
      minLon: bounds.getWest(),
      minLat: bounds.getSouth(),
      maxLon: bounds.getEast(),
      maxLat: bounds.getNorth(),
    };
    this.viewCb({ bbox, zoom: map.getZoom() });
  }
}
