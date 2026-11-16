import fsd from '@feature-sliced/steiger-plugin';
import { defineConfig } from 'steiger';

export default defineConfig([
  ...fsd.configs.recommended,
  {
    // Angular-компонент — цельный файл с шаблоном и стилями, поэтому делить
    // слайсы на сегменты ui/model/api не нужно: границы задают слои и слайсы.
    rules: {
      // Правила границ слоёв и слайсов включаем явно: в recommended они не активны.
      'fsd/forbidden-imports': 'error',
      'fsd/no-cross-imports': 'error',
      'fsd/no-higher-level-imports': 'error',
      'fsd/import-locality': 'error',
      'fsd/no-wildcard-exports': 'error',
      'fsd/no-segmentless-slices': 'off',
      // Слайсы-компоненты часто ссылаются друг на друга через общий widget/page;
      // предупреждение «нет ссылок» не отражает реальную связность Angular-кода.
      'fsd/insignificant-slice': 'off',
      // У shared нет и не должно быть публичного API на уровне сегмента:
      // файлы shared/api/*, shared/ui/* импортируются напрямую.
      'fsd/no-public-api-sidestep': 'off',
    },
  },
  {
    files: ['./src/shared/**'],
    rules: {
      'fsd/public-api': 'off',
      'fsd/no-layer-public-api': 'off',
    },
  },
]);
