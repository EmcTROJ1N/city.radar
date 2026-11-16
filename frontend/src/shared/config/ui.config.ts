// Оформление сайта задаётся здесь, пользователю переключатели не показываются.
// Значения применяются при старте (см. apply-ui-config.ts); сами цвета и размеры — в shared/styles/_tokens.scss.

export type Theme = 'light' | 'dark' | 'system';
export type Accent = 'petrol' | 'iris' | 'amber';
export type Radius = 'sharp' | 'soft' | 'round';
export type Density = 'comfortable' | 'compact';
export type Layout = 'side' | 'top';

export interface UiConfig {
  /** system — следовать настройке устройства. */
  theme: Theme;
  accent: Accent;
  radius: Radius;
  density: Density;
  /** side — боковое меню, top — меню в шапке. */
  layout: Layout;
}

export const UI_CONFIG: UiConfig = {
  theme: 'system',
  accent: 'petrol',
  radius: 'soft',
  density: 'comfortable',
  layout: 'side',
};
