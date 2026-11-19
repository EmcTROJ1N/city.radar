import { describe, expect, it } from 'vitest';
import type { MapItem } from '@shared/map-adapter/map-adapter';
import { initialFocus, pointsBeforeLoad, toTempPoints, zoneIdForPoints } from './map.page';

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

describe('zoneIdForPoints', () => {
  it('возвращает id только для выбранной геозоны', () => {
    expect(zoneIdForPoints(ITEMS, 'zone-1')).toBe('zone-1');
    expect(zoneIdForPoints(ITEMS, 'point-1')).toBeNull();
    expect(zoneIdForPoints(ITEMS, null)).toBeNull();
    expect(zoneIdForPoints(ITEMS, 'нет-такой')).toBeNull();
  });
});

describe('toTempPoints', () => {
  it('переводит ответ API в точки карты', () => {
    const now = Date.parse('2026-11-18T12:00:00Z');
    const points = toTempPoints(
      {
        items: [
          {
            address: 'Тренёва 3в',
            location: { lat: 48.035, lon: 37.78 },
            status: { id: 's1', code: 'weak', name: 'Слабый напор', color: 'limited', icon: 'drop' },
            observedAt: '2026-11-18T11:55:00Z',
            text: 'Тренёва 3в, напор слабый',
            observationCount: 2,
          },
        ],
      },
      now,
    );

    expect(points).toEqual([
      {
        id: 'Тренёва 3в',
        lngLat: [37.78, 48.035],
        tone: 'limited',
        title: 'Тренёва 3в',
        subtitle: 'Слабый напор · 5 мин назад · сообщений: 2',
        text: 'Тренёва 3в, напор слабый',
      },
    ]);
  });
});

describe('pointsBeforeLoad', () => {
  const points = [
    { id: 'a', lngLat: [37.7, 48.0] as [number, number], tone: 'ok' as const, title: 'a', subtitle: '', text: null },
  ];

  it('при обновлении той же зоны оставляет точки до ответа', () => {
    expect(pointsBeforeLoad(points, 'zone-1', 'zone-1')).toBe(points);
  });

  it('при смене зоны или снятии выбора сразу убирает точки прежней зоны', () => {
    expect(pointsBeforeLoad(points, 'zone-1', 'zone-2')).toBeNull();
    expect(pointsBeforeLoad(points, 'zone-1', null)).toBeNull();
  });
});
