export const environment = {
  production: true,
  apiBase: '/api/v1',
  devAuth: false,
  basemapStyle: { light: '/map-style/light.json', dark: '/map-style/dark.json' },
  mapCenter: [37.80, 48.00] as [number, number],
  mapZoom: 11,
  /** Область поиска геозон для стартового вида: [minLon, minLat, maxLon, maxLat]. */
  mapCoverageBbox: [37.2, 47.4, 38.4, 48.6] as [number, number, number, number],
  pollIntervalMs: 45000,
};
