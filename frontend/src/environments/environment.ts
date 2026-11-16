export const environment = {
  production: false,
  apiBase: '/api/v1',
  devAuth: true,
  basemapStyle: { light: '/map-style/light.json', dark: '/map-style/dark.json' },
  mapCenter: [37.80, 48.00] as [number, number],
  mapZoom: 11,
  pollIntervalMs: 45000,
};
