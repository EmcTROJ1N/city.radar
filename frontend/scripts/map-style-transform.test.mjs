import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { ATTRIBUTION, transformStyle } from './map-style-transform.mjs';

const RU_LABEL = ['coalesce', ['get', 'name:ru'], ''];
const EN_LABEL = ['coalesce', ['get', 'name_en'], ['get', 'name']];

function styleWith(layers, sources = {}) {
  return {
    version: 8,
    name: 'OpenFreeMap Positron',
    glyphs: 'https://tiles.openfreemap.org/fonts/{fontstack}/{range}.pbf',
    sprite: 'https://tiles.openfreemap.org/sprites/ofm_f384/ofm',
    sources: {
      openmaptiles: { type: 'vector', url: 'https://tiles.openfreemap.org/planet' },
      ne2_shaded: { type: 'raster', tileSize: 256, tiles: ['https://tiles.openfreemap.org/ne2sr/{z}/{x}/{y}.png'] },
      ...sources,
    },
    layers,
  };
}

const layer = (id, type = 'symbol', layout) => ({ id, type, ...(layout ? { layout } : {}) });

function baseStyle() {
  return styleWith([
    layer('background', 'background'),
    layer('boundary_2', 'line'),
    layer('label_country_1', 'symbol', { 'text-field': EN_LABEL }),
    layer('highway-name-minor', 'symbol', { 'text-field': EN_LABEL }),
    layer('road_shield_us', 'symbol', { 'text-field': ['to-string', ['get', 'ref']] }),
    layer('building_number', 'symbol', { 'text-field': ['to-string', ['get', 'housenumber']] }),
  ]);
}

describe('transformStyle', () => {
  it('оставляет в подписях только name:ru', () => {
    const result = transformStyle(baseStyle(), { name: 'Тест' });
    const street = result.layers.find((item) => item.id === 'highway-name-minor');

    assert.deepEqual(street.layout['text-field'], RU_LABEL);
  });

  it('удаляет слои с номерами дорог (ref)', () => {
    const ids = transformStyle(baseStyle(), { name: 'Тест' }).layers.map((item) => item.id);

    assert.ok(!ids.includes('road_shield_us'));
  });

  it('удаляет границы и подписи стран', () => {
    const ids = transformStyle(baseStyle(), { name: 'Тест' }).layers.map((item) => item.id);

    assert.ok(!ids.includes('boundary_2'));
    assert.ok(!ids.includes('label_country_1'));
    assert.ok(ids.includes('background'));
  });

  it('не трогает подписи домов (housenumber)', () => {
    const input = baseStyle();
    const result = transformStyle(input, { name: 'Тест' });
    const house = result.layers.find((item) => item.id === 'building_number');

    assert.deepEqual(house.layout['text-field'], ['to-string', ['get', 'housenumber']]);
  });

  it('подставляет название и русскую атрибуцию векторного источника', () => {
    const result = transformStyle(baseStyle(), { name: 'Городские ресурсы — светлая' });

    assert.equal(result.name, 'Городские ресурсы — светлая');
    assert.equal(result.sources.openmaptiles.attribution, ATTRIBUTION);
    assert.equal(result.sources.ne2_shaded.attribution, undefined);
    assert.equal(result.sources.ne2_shaded.tiles[0], 'https://tiles.openfreemap.org/ne2sr/{z}/{x}/{y}.png');
  });

  it('не меняет исходный стиль', () => {
    const input = baseStyle();
    transformStyle(input, { name: 'Тест' });

    assert.deepEqual(input.layers.find((item) => item.id === 'highway-name-minor').layout['text-field'], EN_LABEL);
    assert.equal(input.name, 'OpenFreeMap Positron');
  });

  it('самопроверка ловит подпись, читающую name', () => {
    const broken = styleWith([
      layer('building_number', 'symbol', {
        'text-field': ['concat', ['get', 'housenumber'], ' ', ['get', 'name']],
      }),
    ]);

    assert.throws(() => transformStyle(broken, { name: 'Тест' }), /name/);
  });

});
