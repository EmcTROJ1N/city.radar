import { Component } from '@angular/core';
import { onlineSignal } from '@shared/lib/online';
import { Icon } from '@shared/ui/icon';

/** Индикатор отсутствия сети в шапке. */
@Component({
  selector: 'app-offline-indicator',
  imports: [Icon],
  template: `
    @if (!online()) {
      <span class="chip offline" role="status"><app-icon name="alert" />Нет сети</span>
    }`,
  styles: `
    :host { display: inline-flex; }
    .offline { background: color-mix(in srgb, var(--st-none) 22%, transparent); color: var(--bar-ink);
      border: 1px solid color-mix(in srgb, var(--st-none) 70%, transparent); }
  `,
})
export class OfflineIndicator {
  protected readonly online = onlineSignal();
}
