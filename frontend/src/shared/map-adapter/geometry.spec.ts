import { describe, expect, it } from 'vitest';
import { bboxOf, centroid, closeRing, isValidGeometry, isValidPolygon, uniqueVertices } from './geometry';

describe('closeRing', () => {
  it('добавляет первую позицию в конец', () => {
    expect(
      closeRing([
        [37.6, 48.0],
        [37.7, 48.0],
        [37.7, 48.1],
      ]),
    ).toEqual([
      [37.6, 48.0],
      [37.7, 48.0],
      [37.7, 48.1],
      [37.6, 48.0],
    ]);
  });

  it('не дублирует уже замкнутое кольцо', () => {
    const ring: [number, number][] = [
      [0, 0],
      [1, 0],
      [1, 1],
      [0, 0],
    ];
    expect(closeRing(ring)).toEqual(ring);
  });

  it('не меняет исходный массив', () => {
    const points: [number, number][] = [
      [0, 0],
      [1, 0],
      [1, 1],
    ];
    closeRing(points);
    expect(points).toHaveLength(3);
  });

  it('пустой список остаётся пустым', () => {
    expect(closeRing([])).toEqual([]);
  });
});

describe('isValidPolygon', () => {
  it('принимает три разные вершины', () => {
    expect(
      isValidPolygon([
        [0, 0],
        [1, 0],
        [1, 1],
      ]),
    ).toBe(true);
  });

  it('принимает замкнутое кольцо с тремя вершинами', () => {
    expect(
      isValidPolygon([
        [0, 0],
        [1, 0],
        [1, 1],
        [0, 0],
      ]),
    ).toBe(true);
  });

  it('отклоняет повторяющиеся вершины', () => {
    expect(
      isValidPolygon([
        [0, 0],
        [1, 0],
        [0, 0],
        [1, 0],
      ]),
    ).toBe(false);
  });

  it('отклоняет меньше трёх вершин', () => {
    expect(isValidPolygon([])).toBe(false);
    expect(isValidPolygon([[0, 0]])).toBe(false);
    expect(
      isValidPolygon([
        [0, 0],
        [1, 1],
      ]),
    ).toBe(false);
  });
});

describe('uniqueVertices', () => {
  it('убирает повторы, сохраняя порядок', () => {
    expect(
      uniqueVertices([
        [0, 0],
        [1, 0],
        [0, 0],
        [1, 1],
      ]),
    ).toEqual([
      [0, 0],
      [1, 0],
      [1, 1],
    ]);
  });
});

describe('centroid', () => {
  it('для точки совпадает с координатами', () => {
    expect(centroid({ type: 'Point', coordinates: [37.8, 48.01] })).toEqual([37.8, 48.01]);
  });

  it('для полигона считает среднее без двойного веса замыкающей', () => {
    expect(
      centroid({
        type: 'Polygon',
        coordinates: [
          [
            [0, 0],
            [2, 0],
            [2, 2],
            [0, 2],
            [0, 0],
          ],
        ],
      }),
    ).toEqual([1, 1]);
  });

  it('пустой полигон даёт нули', () => {
    expect(centroid({ type: 'Polygon', coordinates: [] })).toEqual([0, 0]);
  });
});

describe('isValidGeometry', () => {
  it('принимает точку в допустимых пределах', () => {
    expect(isValidGeometry({ type: 'Point', coordinates: [37.6, 48.01] })).toBe(true);
    expect(isValidGeometry({ type: 'Point', coordinates: [-180, -90] })).toBe(true);
  });

  it('отклоняет точку с широтой вне диапазона', () => {
    expect(isValidGeometry({ type: 'Point', coordinates: [37.6, 95] })).toBe(false);
    expect(isValidGeometry({ type: 'Point', coordinates: [37.6, -95] })).toBe(false);
  });

  it('отклоняет точку с долготой вне диапазона', () => {
    expect(isValidGeometry({ type: 'Point', coordinates: [190, 48.01] })).toBe(false);
  });

  it('отклоняет точку с NaN', () => {
    expect(isValidGeometry({ type: 'Point', coordinates: [Number.NaN, 48.01] })).toBe(false);
    expect(isValidGeometry({ type: 'Point', coordinates: [37.6, Number.NaN] })).toBe(false);
  });

  it('принимает полигон с корректными вершинами', () => {
    expect(
      isValidGeometry({
        type: 'Polygon',
        coordinates: [
          [
            [37.6, 48.0],
            [37.7, 48.0],
            [37.7, 48.1],
            [37.6, 48.0],
          ],
        ],
      }),
    ).toBe(true);
  });

  it('отклоняет полигон с некорректной вершиной', () => {
    expect(
      isValidGeometry({
        type: 'Polygon',
        coordinates: [
          [
            [37.6, 48.0],
            [37.7, 48.0],
            [37.7, 95],
            [37.6, 48.0],
          ],
        ],
      }),
    ).toBe(false);
  });

  it('отклоняет слишком короткое кольцо', () => {
    expect(
      isValidGeometry({
        type: 'Polygon',
        coordinates: [
          [
            [37.6, 48.0],
            [37.7, 48.0],
            [37.6, 48.0],
          ],
        ],
      }),
    ).toBe(false);
    expect(isValidGeometry({ type: 'Polygon', coordinates: [] })).toBe(false);
  });
});

describe('bboxOf', () => {
  it('для точки вырожденный в точку прямоугольник', () => {
    expect(bboxOf({ type: 'Point', coordinates: [37.8, 48.01] })).toEqual({
      minLon: 37.8,
      minLat: 48.01,
      maxLon: 37.8,
      maxLat: 48.01,
    });
  });

  it('для полигона охватывает все вершины', () => {
    expect(
      bboxOf({
        type: 'Polygon',
        coordinates: [
          [
            [37.6, 48.02],
            [37.7, 47.99],
            [37.65, 48.03],
            [37.6, 48.02],
          ],
        ],
      }),
    ).toEqual({ minLon: 37.6, minLat: 47.99, maxLon: 37.7, maxLat: 48.03 });
  });
});
