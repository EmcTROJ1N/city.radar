import { BBox, LngLat, MapGeometry } from './map-adapter';

function samePoint(a: LngLat, b: LngLat): boolean {
  return a[0] === b[0] && a[1] === b[1];
}

/** Кольцо GeoJSON: первая позиция повторяется в конце. Исходный массив не меняется. */
export function closeRing(points: readonly LngLat[]): LngLat[] {
  if (points.length === 0) return [];
  const copy = points.map((point): LngLat => [point[0], point[1]]);
  const first = copy[0]!;
  const last = copy[copy.length - 1]!;
  if (!samePoint(first, last)) copy.push([first[0], first[1]]);
  return copy;
}

/** Вершины без повторов; закрывающая позиция кольца не считается отдельной вершиной. */
export function uniqueVertices(points: readonly LngLat[]): LngLat[] {
  const seen = new Set<string>();
  const result: LngLat[] = [];
  for (const point of points) {
    const key = `${point[0]},${point[1]}`;
    if (seen.has(key)) continue;
    seen.add(key);
    result.push(point);
  }
  return result;
}

/** Полигон корректен, если у него минимум три разные вершины. */
export function isValidPolygon(points: readonly LngLat[]): boolean {
  return uniqueVertices(points).length >= 3;
}

function isValidPosition(position: LngLat | undefined): boolean {
  if (!position) return false;
  const [lon, lat] = position;
  return (
    Number.isFinite(lon) &&
    Number.isFinite(lat) &&
    lon >= -180 &&
    lon <= 180 &&
    lat >= -90 &&
    lat <= 90
  );
}

/**
 * Геометрия пригодна для MapLibre: координаты конечны и в допустимых пределах,
 * внешнее кольцо полигона содержит минимум четыре позиции.
 */
export function isValidGeometry(geometry: MapGeometry): boolean {
  if (geometry.type === 'Point') return isValidPosition(geometry.coordinates);
  const ring = geometry.coordinates[0];
  if (!ring || ring.length < 4) return false;
  return ring.every((position) => isValidPosition(position));
}

/** Центр для подписи: у полигона — среднее вершин, без двойного веса замыкающей. */
export function centroid(geometry: MapGeometry): LngLat {
  if (geometry.type === 'Point') return [geometry.coordinates[0], geometry.coordinates[1]];
  const ring = geometry.coordinates[0] ?? [];
  if (ring.length === 0) return [0, 0];
  const vertices = ring.length > 1 && samePoint(ring[0]!, ring[ring.length - 1]!)
    ? ring.slice(0, -1)
    : ring;
  let lon = 0;
  let lat = 0;
  for (const [x, y] of vertices) {
    lon += x;
    lat += y;
  }
  return [lon / vertices.length, lat / vertices.length];
}

export function bboxOf(geometry: MapGeometry): BBox {
  if (geometry.type === 'Point') {
    const [lon, lat] = geometry.coordinates;
    return { minLon: lon, minLat: lat, maxLon: lon, maxLat: lat };
  }
  const points = geometry.coordinates.flat();
  if (points.length === 0) return { minLon: 0, minLat: 0, maxLon: 0, maxLat: 0 };
  let minLon = Infinity;
  let minLat = Infinity;
  let maxLon = -Infinity;
  let maxLat = -Infinity;
  for (const [lon, lat] of points) {
    minLon = Math.min(minLon, lon);
    minLat = Math.min(minLat, lat);
    maxLon = Math.max(maxLon, lon);
    maxLat = Math.max(maxLat, lat);
  }
  return { minLon, minLat, maxLon, maxLat };
}

/** Общая рамка набора геометрий; null для пустого списка. */
export function bboxOfMany(geometries: readonly MapGeometry[]): BBox | null {
  let box: BBox | null = null;
  for (const geometry of geometries) {
    const current = bboxOf(geometry);
    if (!box) {
      box = { ...current };
      continue;
    }
    box.minLon = Math.min(box.minLon, current.minLon);
    box.minLat = Math.min(box.minLat, current.minLat);
    box.maxLon = Math.max(box.maxLon, current.maxLon);
    box.maxLat = Math.max(box.maxLat, current.maxLat);
  }
  return box;
}
