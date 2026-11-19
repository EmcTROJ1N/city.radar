import { describe, expect, it } from 'vitest';
import type { MapObject, ObjectType } from '@shared/api/types';
import {
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
    expect(value.isActive).toBe(true);
    expect(value.sourceIds).toEqual(['src-1', 'src-2']);
  });

  it('не отдаёт ссылки на массивы исходной фиксации', () => {
    const value = formFromObject(OBJECT);
    value.sourceIds.push('src-3');

    expect(OBJECT.sourceIds).toEqual(['src-1', 'src-2']);
  });
});

describe('inputFromForm', () => {
  it('без типа объекта бросает ошибку — тип обязателен', () => {
    const value = {
      ...emptyMapObjectForm(),
      name: 'Колонка',
      coverageKind: 'point' as const,
      geometry: { type: 'Point' as const, coordinates: [37.8, 48.01] as [number, number] },
    };

    expect(() => inputFromForm(value)).toThrow('Не задан тип объекта');
  });

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
      address: 'ул. Садовая, 1',
      isActive: true,
      sourceIds: ['src-1', 'src-2'],
    });
  });

  it('для update не передаёт sourceIds', () => {
    const value = {
      ...emptyMapObjectForm(),
      name: 'Точка',
      objectTypeId: 'type-1',
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

describe('geometryMismatch', () => {
  const type = (geometryKind: ObjectType['geometryKind']): ObjectType => ({
    id: 't',
    categoryId: 'c',
    code: 'code',
    name: 'Тип',
    geometryKind,
  });

  it('предупреждает только при конкретном несовпадающем виде', () => {
    expect(geometryMismatch(type('point'), 'polygon')).toBe(true);
    expect(geometryMismatch(type('polygon'), 'polygon')).toBe(false);
    expect(geometryMismatch(type('any'), 'point')).toBe(false);
    expect(geometryMismatch(null, 'point')).toBe(false);
  });
});
