export const environment = {
  production: true,
  apiBase: '/api/v1',
  devAuth: false,
  basemapStyle: { light: '/map-style/light.json', dark: '/map-style/dark.json' },
  mapCenter: [37.80, 48.00] as [number, number],
  mapZoom: 11,
  pollIntervalMs: 45000,
};
