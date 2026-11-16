import { LngLat } from './map-adapter';

/** Позиция пользователя, пригодная для маркера: координаты и радиус точности в метрах. */
export interface UserLocation {
  coordinates: LngLat;
  accuracy: number;
}

interface PositionLike {
  coords: {
    longitude: number;
    latitude: number;
    accuracy?: number;
  };
}

function inRange(value: number, min: number, max: number): boolean {
  return Number.isFinite(value) && value >= min && value <= max;
}

/**
 * Позиция из Geolocation API в координаты карты. «Мусорные» значения (NaN, выход
 * за пределы WGS 84) отбрасываем: иначе MapLibre нарисует маркер в случайной точке.
 */
export function userLocationFrom(position: PositionLike): UserLocation | null {
  const { longitude, latitude, accuracy } = position.coords;
  if (!inRange(longitude, -180, 180) || !inRange(latitude, -90, 90)) return null;
  return {
    coordinates: [longitude, latitude],
    accuracy: Number.isFinite(accuracy) ? (accuracy ?? 0) : 0,
  };
}

/** Понятный текст ошибки GeopositionError вместо кода браузера. */
export function geolocationErrorMessage(error: { code: number } | null | undefined): string {
  switch (error?.code) {
    case 1:
      return 'Доступ к геолокации запрещён. Разрешите его в настройках браузера.';
    case 2:
      return 'Не удалось определить местоположение.';
    case 3:
      return 'Определение местоположения заняло слишком много времени.';
    default:
      return 'Не удалось получить доступ к геолокации.';
  }
}
