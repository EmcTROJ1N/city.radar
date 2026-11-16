import { Component, input } from '@angular/core';
import { Tone } from '../lib/tone';
import { Icon, IconName } from './icon';

/** Статус всегда показывается цветом, иконкой и подписью вместе. */
@Component({
  selector: 'app-status-badge',
  imports: [Icon],
  template: `<span class="status" [class]="'tone-' + tone()" [class.is-stale]="stale()"><app-icon [name]="icon()" />{{ label() }}</span>`,
})
export class StatusBadge {
  readonly label = input.required<string>();
  readonly tone = input<Tone>('stale');
  readonly icon = input<IconName>('question');
  readonly stale = input(false);
}
