import { describe, expect, it } from 'vitest';
import { geolocationErrorMessage, userLocationFrom } from './geolocation';

describe('userLocationFrom', () => {
  it('переводит позицию браузера в координаты карты', () => {
    expect(
      userLocationFrom({ coords: { longitude: 37.81, latitude: 48.02, accuracy: 12.6 } }),
    ).toEqual({ coordinates: [37.81, 48.02], accuracy: 12.6 });
  });

  it('без точности считает её нулевой', () => {
    expect(userLocationFrom({ coords: { longitude: 37.81, latitude: 48.02 } })).toEqual({
      coordinates: [37.81, 48.02],
      accuracy: 0,
    });
    expect(
      userLocationFrom({ coords: { longitude: 37.81, latitude: 48.02, accuracy: Number.NaN } })
        ?.accuracy,
    ).toBe(0);
  });

  it('отбрасывает NaN и координаты вне WGS 84', () => {
    expect(
      userLocationFrom({ coords: { longitude: Number.NaN, latitude: 48.02 } }),
    ).toBeNull();
    expect(
      userLocationFrom({ coords: { longitude: 37.81, latitude: 95 } }),
    ).toBeNull();
    expect(
      userLocationFrom({ coords: { longitude: 190, latitude: 48.02 } }),
    ).toBeNull();
  });
});

describe('geolocationErrorMessage', () => {
  it('объясняет отказ в доступе', () => {
    expect(geolocationErrorMessage({ code: 1 })).toContain('запрещён');
  });

  it('различает недоступную позицию и таймаут', () => {
    expect(geolocationErrorMessage({ code: 2 })).toContain('определить');
    expect(geolocationErrorMessage({ code: 3 })).toContain('времени');
  });

  it('даёт общий текст для неизвестной ошибки', () => {
    expect(geolocationErrorMessage(null)).toContain('геолокац');
    expect(geolocationErrorMessage({ code: 99 })).toContain('геолокац');
  });
});
