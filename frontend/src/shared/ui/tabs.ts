import { Component, ElementRef, computed, input, model, viewChildren } from '@angular/core';
import { Icon, IconName } from './icon';

export interface TabItem {
  id: string;
  label: string;
  icon?: IconName;
}

/** Переключатель разделов; синхронизацию с адресом делает страница. */
@Component({
  selector: 'app-tabs',
  imports: [Icon],
  template: `
    <div class="tabs" role="tablist" (keydown)="onKey($event)">
      @for (tab of tabs(); track tab.id; let i = $index) {
        <button
          #tabBtn
          type="button"
          role="tab"
          [attr.aria-selected]="tab.id === active()"
          [attr.tabindex]="tab.id === active() ? 0 : -1"
          (click)="active.set(tab.id)"
        >
          @if (tab.icon; as icon) { <app-icon [name]="icon" /> }
          {{ tab.label }}
        </button>
      }
    </div>`,
  styles: `
    :host { display: block; }
    .tabs button { display: inline-flex; align-items: center; gap: 6px; }
  `,
})
export class Tabs {
  readonly tabs = input.required<TabItem[]>();
  readonly active = model.required<string>();

  private readonly buttons = viewChildren<ElementRef<HTMLButtonElement>>('tabBtn');

  protected onKey(event: KeyboardEvent): void {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
    const tabs = this.tabs();
    if (!tabs.length) return;
    event.preventDefault();
    const current = Math.max(0, tabs.findIndex((tab) => tab.id === this.active()));
    const step = event.key === 'ArrowRight' ? 1 : -1;
    const next = (current + step + tabs.length) % tabs.length;
    const target = tabs[next];
    if (!target) return;
    this.active.set(target.id);
    this.buttons()[next]?.nativeElement.focus();
  }
}
