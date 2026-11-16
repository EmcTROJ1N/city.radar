import { describe, expect, it } from 'vitest';
import type { MapObject, ObjectType } from '@shared/api/types';
import {
  attributeFields,
  emptyMapObjectForm,
  formFromObject,
  geometryMismatch,
  inputFromForm,
  polygonVertexCount,
  sourceDiff,
  toApiGeometry,
  toMapGeometry,
} from './map-object-form.model';

const OBJECT: MapObject = {
  id: 'obj-1',
  name: 'Колонка на Ленинском',
  objectTypeId: 'type-1',
  groupId: null,
  coverageKind: 'point',
  geometry: { type: 'Point', coordinates: [37.8, 48.01] },
  attributes: { pressure: 3 },
  address: 'пр. Ленинский, 12',
  isActive: true,
  sourceIds: ['src-1', 'src-2'],
};

describe('formFromObject', () => {
  it('переносит поля фиксации в форму', () => {
    const value = formFromObject(OBJECT);

    expect(value.name).toBe(OBJECT.name);
    expect(value.coverageKind).toBe('point');
    expect(value.geometry).toEqual({ type: 'Point', coordinates: [37.8, 48.01] });
    expect(value.objectTypeId).toBe('type-1');
    expect(value.groupId).toBeNull();
    expect(value.address).toBe('пр. Ленинский, 12');
    expect(value.attributes).toEqual({ pressure: 3 });
    expect(value.isActive).toBe(true);
    expect(value.sourceIds).toEqual(['src-1', 'src-2']);
  });

  it('не отдаёт ссылки на массивы и атрибуты исходной фиксации', () => {
    const value = formFromObject(OBJECT);
    value.sourceIds.push('src-3');
    value.attributes['pressure'] = 1;

    expect(OBJECT.sourceIds).toEqual(['src-1', 'src-2']);
    expect(OBJECT.attributes['pressure']).toBe(3);
  });
});

describe('inputFromForm', () => {
  it('собирает MapObjectInput и обрезает тексты', () => {
    const value = {
      ...emptyMapObjectForm(),
      name: '  Колонка ',
      coverageKind: 'point' as const,
      geometry: { type: 'Point' as const, coordinates: [37.8, 48.01] as [number, number] },
      objectTypeId: 'type-1',
      address: '  ул. Садовая, 1 ',
      sourceIds: ['src-1', 'src-1', 'src-2'],
    };

    expect(inputFromForm(value)).toEqual({
      name: 'Колонка',
      objectTypeId: 'type-1',
      groupId: null,
      coverageKind: 'point',
      geometry: { type: 'Point', coordinates: [37.8, 48.01] },
      attributes: {},
      address: 'ул. Садовая, 1',
      isActive: true,
      sourceIds: ['src-1', 'src-2'],
    });
  });

  it('для update не передаёт sourceIds', () => {
    const value = {
      ...emptyMapObjectForm(),
      name: 'Точка',
      geometry: { type: 'Point' as const, coordinates: [37.8, 48.01] as [number, number] },
      sourceIds: ['src-1'],
    };

    expect(inputFromForm(value, { withSources: false })).not.toHaveProperty('sourceIds');
  });

  it('без геометрии сообщает об ошибке', () => {
    expect(() => inputFromForm(emptyMapObjectForm())).toThrow('Не задана геометрия');
  });
});

describe('геометрия', () => {
  it('замыкает кольцо полигона при отправке', () => {
    const api = toApiGeometry({
      type: 'Polygon',
      coordinates: [
        [
          [37.6, 48],
          [37.7, 48],
          [37.7, 48.1],
        ],
      ],
    });

    expect(api).toEqual({
      type: 'Polygon',
      coordinates: [
        [
          [37.6, 48],
          [37.7, 48],
          [37.7, 48.1],
          [37.6, 48],
        ],
      ],
    });
  });

  it('читает геометрию API в геометрию карты', () => {
    expect(toMapGeometry({ type: 'Point', coordinates: [37.8, 48.01] })).toEqual({
      type: 'Point',
      coordinates: [37.8, 48.01],
    });
    expect(toMapGeometry({ type: 'Polygon', coordinates: [] })).toBeNull();
  });

  it('считает вершины без замыкающей', () => {
    expect(
      polygonVertexCount({
        type: 'Polygon',
        coordinates: [
          [
            [0, 0],
            [1, 0],
            [1, 1],
            [0, 0],
          ],
        ],
      }),
    ).toBe(3);
  });
});

describe('sourceDiff', () => {
  it('находит привязки и отвязки', () => {
    expect(sourceDiff(['a', 'b'], ['b', 'c'])).toEqual({ toLink: ['c'], toUnlink: ['a'] });
  });

  it('без изменений возвращает пустые списки', () => {
    expect(sourceDiff(['a'], ['a'])).toEqual({ toLink: [], toUnlink: [] });
  });
});

describe('attributeFields', () => {
  it('разбирает свойства JSON Schema', () => {
    expect(
      attributeFields({
        type: 'object',
        properties: {
          pressure: { type: 'number', title: 'Давление' },
          note: { type: 'string' },
          broken: { type: 'boolean' },
          mode: { type: 'string', enum: ['auto', 'manual'], title: 'Режим' },
        },
      }),
    ).toEqual([
      { key: 'pressure', label: 'Давление', kind: 'number', options: [] },
      { key: 'note', label: 'note', kind: 'string', options: [] },
      { key: 'broken', label: 'broken', kind: 'boolean', options: [] },
      {
        key: 'mode',
        label: 'Режим',
        kind: 'enum',
        options: [
          { value: 'auto', label: 'auto' },
          { value: 'manual', label: 'manual' },
        ],
      },
    ]);
  });

  it('без properties возвращает пустой список', () => {
    expect(attributeFields(null)).toEqual([]);
    expect(attributeFields({ type: 'object' })).toEqual([]);
  });
});

describe('geometryMismatch', () => {
  const type = (geometryKind: ObjectType['geometryKind']): ObjectType => ({
    id: 't',
    categoryId: 'c',
    code: 'code',
    name: 'Тип',
    geometryKind,
    attributesSchema: {},
  });

  it('предупреждает только при конкретном несовпадающем виде', () => {
    expect(geometryMismatch(type('point'), 'polygon')).toBe(true);
    expect(geometryMismatch(type('polygon'), 'polygon')).toBe(false);
    expect(geometryMismatch(type('any'), 'point')).toBe(false);
    expect(geometryMismatch(null, 'point')).toBe(false);
  });
});
