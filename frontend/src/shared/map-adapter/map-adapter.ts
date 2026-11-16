import { Tone } from '../lib/tone';
import { IconName } from '../ui/icon';

/** [долгота, широта] — порядок координат как в GeoJSON и MapLibre. */
export type LngLat = [number, number];

export interface BBox {
  minLon: number;
  minLat: number;
  maxLon: number;
  maxLat: number;
}

export interface MapView {
  bbox: BBox;
  zoom: number;
}

export type MapGeometry =
  | { type: 'Point'; coordinates: LngLat }
  | { type: 'Polygon'; coordinates: LngLat[][] };

/** Готовый к отрисовке элемент карты: адаптер не знает про API, страница — про движок. */
export interface MapItem {
  id: string;
  name: string;
  geometry: MapGeometry;
  /** Цвет статуса. */
  tone: Tone;
  /** Имя иконки статуса из API, нормализованное в IconName. */
  icon: IconName;
  /** Текстовая подпись статуса, «Неизвестно» если статуса нет. */
  label: string;
  /** Устарел или неизвестен — «блёкнет». */
  stale: boolean;
}

export type DrawMode = 'none' | 'point' | 'polygon';

/** Стили подложки по теме оформления; обе пустые строки — пустой стиль с фоном. */
export interface MapStyleUrls {
  light: string;
  dark: string;
}

export interface MapAdapter {
  mount(
    container: HTMLElement,
    opts: { center: LngLat; zoom: number; styleUrls: MapStyleUrls },
  ): Promise<void>;
  destroy(): void;
  setItems(items: MapItem[]): void;
  setSelected(id: string | null): void;
  flyTo(geometry: MapGeometry): void;
  onSelect(cb: (id: string) => void): void;
  /** После moveend с задержкой 300 мс и один раз после загрузки. */
  onViewChange(cb: (view: MapView) => void): void;
  /** Режим рисования; `initial` сразу задаёт точку или готовый полигон. */
  setDraw(mode: DrawMode, initial?: MapGeometry | null): void;
  undoVertex(): void;
  clearDraft(): void;
  /** Черновик в виде корректной геометрии; незамкнутый полигон — null. */
  onDrawChange(cb: (geometry: MapGeometry | null) => void): void;
}

/** `'minLon,minLat,maxLon,maxLat'` с 5 знаками после запятой. */
export function bboxParam(b: BBox): string {
  const round = (n: number) => n.toFixed(5);
  return [b.minLon, b.minLat, b.maxLon, b.maxLat].map(round).join(',');
}

