import { describe, expect, it } from 'vitest';
import type { MapItem } from '../map-adapter';
import {
  applyMarkerClasses,
  overlayBeforeLayerId,
  tempPointElement,
  tempPointPopupContent,
  zoneFeatureCollection,
} from './maplibre-adapter';

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

describe('overlayBeforeLayerId', () => {
  it('ставит свои слои над зданиями и дорогами, но под подписями', () => {
    // Порядок как в public/map-style/dark.json: вода, здания, дороги, затем подписи.
    const layers = [
      { id: 'background', type: 'background' },
      { id: 'water_name', type: 'symbol' },
      { id: 'building', type: 'fill' },
      { id: 'highway_minor', type: 'line' },
      { id: 'highway_name_other', type: 'symbol' },
      { id: 'place_city', type: 'symbol' },
    ];

    expect(overlayBeforeLayerId(layers)).toBe('highway_name_other');
  });

  it('без подписей возвращает undefined — слои встают поверх подложки', () => {
    const layers = [
      { id: 'background', type: 'background' },
      { id: 'building', type: 'fill' },
    ];

    expect(overlayBeforeLayerId(layers)).toBeUndefined();
  });

  it('подложка только из подписей: вставляем перед первой', () => {
    const layers = [{ id: 'place_city', type: 'symbol' }];

    expect(overlayBeforeLayerId(layers)).toBe('place_city');
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

describe('tempPointElement', () => {
  const point = {
    id: 'тренева 3в',
    lngLat: [37.78, 48.035] as [number, number],
    tone: 'limited' as const,
    title: 'Тренёва 3в',
    subtitle: 'Слабый напор · 5 мин назад',
    text: '<b>Тренёва 3в</b>, 5 этаж, напор слабый',
  };

  it('создаёт кнопку с цветом статуса и подписью для экранного диктора', () => {
    const element = tempPointElement(point);

    expect(element.tagName).toBe('BUTTON');
    expect(element.classList.contains('map-temp-point')).toBe(true);
    expect(element.classList.contains('tone-limited')).toBe(true);
    expect(element.getAttribute('aria-label')).toBe('Тренёва 3в: Слабый напор · 5 мин назад');
  });

  it('подсказка — адрес, статус и текст сообщения как текст, без разметки', () => {
    const content = tempPointPopupContent(point);

    expect(content.textContent).toContain('Тренёва 3в');
    expect(content.textContent).toContain('Слабый напор · 5 мин назад');
    expect(content.textContent).toContain('<b>Тренёва 3в</b>, 5 этаж, напор слабый');
    expect(content.querySelector('b')).toBeNull();
  });

  it('подсказка без текста сообщения не содержит пустую цитату', () => {
    const content = tempPointPopupContent({ ...point, text: null });

    expect(content.querySelector('blockquote')).toBeNull();
  });
});
