import { describe, expect, it } from 'vitest';
import type { MapItem } from '../map-adapter';
import { applyMarkerClasses, zoneFeatureCollection } from './maplibre-adapter';

const ZONE: MapItem = {
  id: '1d9f4c6e-4f3a-4c1e-9a0e-77b2c5a1e001',
  name: 'Насосная станция «Северная»',
  geometry: {
    type: 'Polygon',
    coordinates: [
      [
        [37.6, 48.0],
        [37.62, 48.0],
        [37.62, 48.02],
        [37.6, 48.02],
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
  id: '2b7c8d90-1111-4222-8333-944455556666',
  name: 'Колонка',
  geometry: { type: 'Point', coordinates: [37.61, 48.01] },
  tone: 'none',
  icon: 'cross',
  label: 'Авария',
  stale: true,
};

describe('zoneFeatureCollection', () => {
  it('кладёт строковый id фиксации в properties', () => {
    const collection = zoneFeatureCollection([ZONE]);

    expect(collection.type).toBe('FeatureCollection');
    expect(collection.features).toHaveLength(1);
    expect(collection.features[0]?.properties?.['id']).toBe(ZONE.id);
    expect(collection.features[0]?.properties?.['name']).toBe(ZONE.name);
    expect(collection.features[0]?.properties?.['tone']).toBe('ok');
    expect(collection.features[0]?.properties?.['stale']).toBe(false);
  });

  it('переносит цвет статуса и признак устаревания', () => {
    const staleZone: MapItem = { ...ZONE, id: 'stale-zone', tone: 'limited', stale: true };

    const [fresh, stale] = zoneFeatureCollection([ZONE, staleZone]).features;

    expect(fresh?.properties?.['tone']).toBe('ok');
    expect(fresh?.properties?.['stale']).toBe(false);
    expect(stale?.properties?.['id']).toBe('stale-zone');
    expect(stale?.properties?.['tone']).toBe('limited');
    expect(stale?.properties?.['stale']).toBe(true);
  });

  it('не включает точечные фиксации', () => {
    const collection = zoneFeatureCollection([ZONE, POINT]);

    expect(collection.features).toHaveLength(1);
    expect(collection.features[0]?.properties?.['id']).toBe(ZONE.id);
    expect(collection.features.every((feature) => feature.geometry.type === 'Polygon')).toBe(true);
  });

  it('возвращает пустую коллекцию без полигонов', () => {
    expect(zoneFeatureCollection([POINT]).features).toHaveLength(0);
    expect(zoneFeatureCollection([]).features).toHaveLength(0);
  });
});

describe('applyMarkerClasses', () => {
  it('сохраняет служебный класс maplibregl-marker (position: absolute)', () => {
    const element = document.createElement('div');
    element.className = 'map-zone-label maplibregl-marker maplibregl-marker-anchor-center';

    applyMarkerClasses(element, ['map-zone-label', 'tone-none']);

    expect(element.classList.contains('maplibregl-marker')).toBe(true);
    expect(element.classList.contains('maplibregl-marker-anchor-center')).toBe(true);
  });

  it('заменяет классы состояния, не накапливая их', () => {
    const element = document.createElement('div');
    element.className = 'map-zone-label tone-ok is-hidden';
    element.classList.add('maplibregl-marker');

    applyMarkerClasses(element, ['map-zone-label', 'tone-none', 'is-selected']);

    expect(element.classList.contains('tone-ok')).toBe(false);
    expect(element.classList.contains('is-hidden')).toBe(false);
    expect(element.classList.contains('tone-none')).toBe(true);
    expect(element.classList.contains('is-selected')).toBe(true);
    expect(element.classList.contains('maplibregl-marker')).toBe(true);
  });

  it('не трогает посторонние классы', () => {
    const element = document.createElement('div');
    element.className = 'maplibregl-marker custom';

    applyMarkerClasses(element, ['map-pin', 'tone-ok']);

    expect(element.classList.contains('custom')).toBe(true);
    expect(element.classList.contains('map-pin')).toBe(true);
  });
});
