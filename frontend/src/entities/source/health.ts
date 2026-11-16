import type { SourceState } from '@shared/api/types';
import type { Tone } from '@shared/lib/tone';
import type { IconName } from '@shared/ui/icon';

/** Цвет и иконка состояния источника: ok — синий, ошибки — красный, FloodWait — жёлтый. */
export function sourceStateTone(state: SourceState): Tone {
  switch (state) {
    case 'ok':
      return 'ok';
    case 'flood_wait':
      return 'limited';
    case 'auth_error':
    case 'error':
      return 'none';
    default:
      return 'stale';
  }
}

export function sourceStateIcon(state: SourceState): IconName {
  switch (state) {
    case 'ok':
      return 'check';
    case 'flood_wait':
    case 'silent':
      return 'clock';
    case 'auth_error':
      return 'lock';
    case 'error':
      return 'alert';
    default:
      return 'question';
  }
}
