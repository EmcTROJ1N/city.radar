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
import { DrawMode, MapAdapter, MapGeometry, MapItem, MapView } from './map-adapter';
import { MapLibreAdapter } from './maplibre/maplibre-adapter';

/** Карта на MapLibre: принимает готовые MapItem, движок не знает про API. */
@Component({
  selector: 'app-map-view',
  template: `<div class="map" #host></div>`,
  styles: `
    :host { display: block; height: 100%; }
    .map { height: 100%; min-height: 320px; background: var(--map-land); }
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

  readonly selectItem = output<string>();
  readonly viewChange = output<MapView>();
  readonly drawChange = output<MapGeometry | null>();

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
  }
}
