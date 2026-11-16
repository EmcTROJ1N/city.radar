import {
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { environment } from '@env/environment';
import { Icon } from '../ui/icon';
import { DrawMode, MapAdapter, MapGeometry, MapItem, MapView } from './map-adapter';
import { MapLibreAdapter } from './maplibre/maplibre-adapter';

/** Карта на MapLibre: принимает готовые MapItem, движок не знает про API. */
@Component({
  selector: 'app-map-view',
  imports: [Icon],
  template: `
    <div class="map" #host></div>
    @if (userLocation()) {
      <button
        class="map-locate"
        type="button"
        (click)="locateUser()"
        aria-label="Показать моё местоположение"
        title="Моё местоположение"
      >
        <app-icon name="locate" />
      </button>
    }
  `,
  styles: `
    :host { display: block; position: relative; height: 100%; }
    .map { height: 100%; min-height: 320px; background: var(--map-land); }
    .map-locate {
      position: absolute;
      right: 10px;
      bottom: 28px;
      z-index: 2;
      display: grid;
      place-items: center;
      width: 38px;
      height: 38px;
      border: 1px solid var(--line);
      border-radius: var(--r-md);
      background: var(--surface);
      color: var(--ink);
      box-shadow: var(--shadow-float);
      cursor: pointer;
    }
    .map-locate:hover { background: var(--surface-2); }
    .map-locate:focus-visible { box-shadow: var(--focus-ring), var(--shadow-float); }
  `,
})
export class MapViewComponent {
  private readonly host = viewChild.required<ElementRef<HTMLElement>>('host');
  private adapter: MapAdapter | null = null;
  private readonly ready = signal(false);

  readonly items = input<MapItem[]>([]);
  readonly selectedId = input<string | null>(null);
  readonly focus = input<MapGeometry | null>(null);
  readonly drawMode = input<DrawMode>('none');
  readonly drawInitial = input<MapGeometry | null>(null);
  /** Показывать свою позицию: браузер спросит доступ к геолокации. */
  readonly userLocation = input(false);
  /** Стартовый вид по набору геометрий (например, по всем геозонам). */
  readonly fitGeometries = input<readonly MapGeometry[] | null>(null);

  readonly selectItem = output<string>();
  readonly viewChange = output<MapView>();
  readonly drawChange = output<MapGeometry | null>();
  readonly locationError = output<string>();

  /** Центрирует карту на своей позиции (или запрашивает её, если ещё нет). */
  locateUser(): void {
    this.adapter?.flyToUser();
  }

  /** Отменяет последнюю вершину полигона. */
  undoVertex(): void {
    this.adapter?.undoVertex();
  }

  clearDraft(): void {
    this.adapter?.clearDraft();
  }

  constructor() {
    afterNextRender(() => {
      const adapter = new MapLibreAdapter();
      this.adapter = adapter;
      adapter.onSelect((id) => this.selectItem.emit(id));
      adapter.onViewChange((view) => this.viewChange.emit(view));
      adapter.onDrawChange((geometry) => this.drawChange.emit(geometry));
      adapter.onLocationError((message) => this.locationError.emit(message));
      adapter.setItems(this.items());
      adapter.setSelected(this.selectedId());
      adapter
        .mount(this.host().nativeElement, {
          center: environment.mapCenter,
          zoom: environment.mapZoom,
          styleUrls: environment.basemapStyle,
        })
        .then(() => {
          this.ready.set(true);
          adapter.setDraw(this.drawMode(), this.drawInitial());
          if (this.focus()) adapter.flyTo(this.focus()!);
          const geometries = this.fitGeometries();
          if (geometries?.length) adapter.fitGeometries(geometries);
          if (this.userLocation()) adapter.startGeolocation();
        })
        .catch((err) => console.warn('Карта: не удалось запустить MapLibre', err));
    });

    inject(DestroyRef).onDestroy(() => this.adapter?.destroy());

    effect(() => {
      if (!this.ready()) return;
      this.adapter?.setItems(this.items());
    });
    effect(() => {
      if (!this.ready()) return;
      this.adapter?.setSelected(this.selectedId());
    });
    effect(() => {
      const focus = this.focus();
      if (!this.ready() || !focus) return;
      this.adapter?.flyTo(focus);
    });
    effect(() => {
      const mode = this.drawMode();
      const initial = this.drawInitial();
      if (!this.ready()) return;
      this.adapter?.setDraw(mode, initial);
    });
    effect(() => {
      const enabled = this.userLocation();
      if (!this.ready() || !enabled) return;
      this.adapter?.startGeolocation();
    });
    effect(() => {
      const geometries = this.fitGeometries();
      if (!this.ready() || !geometries?.length) return;
      this.adapter?.fitGeometries(geometries);
    });
  }
}
