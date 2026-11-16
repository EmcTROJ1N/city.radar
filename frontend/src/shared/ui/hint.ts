import { Component, input } from '@angular/core';
import { Icon } from './icon';

let seq = 0;

/** Короткая подсказка: появляется при наведении курсора или фокусе с клавиатуры. */
@Component({
  selector: 'app-hint',
  imports: [Icon],
  template: `
    <span class="wrap">
      <button type="button" class="btn-i" aria-label="Подсказка" [attr.aria-describedby]="id"><app-icon name="info" /></button>
      <span class="tip" role="tooltip" [id]="id">{{ text() }}</span>
    </span>`,
  styles: `
    .wrap { position: relative; display: inline-flex; }
    .btn-i { all: unset; display: inline-flex; cursor: help; color: var(--ink-3); border-radius: 50%; }
    .btn-i:hover, .btn-i:focus-visible { color: var(--brand); }
    .tip { display: none; position: absolute; bottom: calc(100% + 8px); left: -8px; width: 280px; z-index: 40;
      padding: 8px 10px; border-radius: var(--r-md); background: var(--ink); color: var(--bg); font-size: 13px; line-height: 1.4; box-shadow: var(--shadow-float); }
    .wrap:hover .tip, .btn-i:focus-visible + .tip { display: block; }
  `,
})
export class Hint {
  readonly text = input.required<string>();
  protected readonly id = `hint-${++seq}`;
}
