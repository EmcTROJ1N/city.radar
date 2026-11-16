import { describe, expect, it } from 'vitest';
import type { MapItem } from '@shared/map-adapter/map-adapter';
import { initialFocus } from './map.page';

const POLYGON: MapItem = {
  id: 'zone-1',
  name: 'Насосная станция',
  geometry: {
    type: 'Polygon',
    coordinates: [
      [
        [37.6, 48.0],
        [37.62, 48.0],
        [37.62, 48.02],
        [37.6, 48.0],
      ],
    ],
  },
  tone: 'ok',
  icon: 'drop',
  label: 'Работает',
  stale: false,
};

const POINT: MapItem = {
  id: 'point-1',
  name: 'Колонка',
  geometry: { type: 'Point', coordinates: [37.61, 48.01] },
  tone: 'none',
  icon: 'cross',
  label: 'Авария',
  stale: true,
};

const ITEMS = [POLYGON, POINT];

describe('initialFocus', () => {
  it('возвращает геометрию фиксации с нужным id', () => {
    expect(initialFocus(ITEMS, 'zone-1')).toEqual(POLYGON.geometry);
    expect(initialFocus(ITEMS, 'point-1')).toEqual(POINT.geometry);
  });

  it('возвращает null, если такой фиксации нет или id не задан', () => {
    expect(initialFocus(ITEMS, 'нет-такой')).toBeNull();
    expect(initialFocus(ITEMS, null)).toBeNull();
    expect(initialFocus([], 'zone-1')).toBeNull();
  });
});
