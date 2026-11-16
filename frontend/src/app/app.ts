import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { SwUpdate } from '@angular/service-worker';
import { SessionService } from '@entities/user';
import { ADMIN_PERMISSIONS, CATALOG_PERMISSIONS, P, Permission } from '@shared/lib/permissions';
import { Icon, IconName } from '@shared/ui/icon';
import { Toasts } from '@shared/ui/toast';
import { ConfirmHost } from '@shared/ui/confirm';
import { OfflineIndicator } from './offline-indicator';

interface NavItem { path: string; label: string; icon: IconName; anyOf: Permission[]; }

const NAV: NavItem[] = [
  { path: '/', label: 'Карта', icon: 'map', anyOf: [] },
  { path: '/sources', label: 'Источники', icon: 'send', anyOf: [P.sourceManage] },
  { path: '/map-objects', label: 'Фиксации на карте', icon: 'polygon', anyOf: [P.objectManage] },
  { path: '/catalog', label: 'Каталог', icon: 'book', anyOf: [...CATALOG_PERMISSIONS] },
  { path: '/moderation', label: 'Модерация', icon: 'inbox', anyOf: [P.moderationProcess] },
  { path: '/admin', label: 'Админ', icon: 'shield', anyOf: [...ADMIN_PERMISSIONS] },
];

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, Icon, Toasts, ConfirmHost, OfflineIndicator],
  templateUrl: './app.html',
  styles: `
    .update { position: fixed; left: 50%; bottom: var(--gap); transform: translateX(-50%); z-index: 70;
      display: flex; align-items: center; gap: 12px; width: min(420px, calc(100vw - var(--gap) * 2));
      padding: 10px 10px 10px 16px; border-radius: var(--r-md); background: var(--surface); color: var(--ink);
      border: 1px solid var(--line); box-shadow: var(--shadow-float); }
  `,
})
export class App {
  protected readonly session = inject(SessionService);
  protected readonly nav = computed(() =>
    NAV.filter((n) => n.anyOf.length === 0 || this.session.hasAny(n.anyOf)),
  );
  protected readonly navOpen = signal(false);
  protected readonly updateReady = signal(false);
  private readonly swUpdate = inject(SwUpdate, { optional: true });

  constructor() {
    const sw = this.swUpdate;
    if (sw?.isEnabled) {
      sw.versionUpdates.pipe(takeUntilDestroyed()).subscribe((event) => {
        if (event.type === 'VERSION_READY') this.updateReady.set(true);
      });
    }
  }

  protected reload(): void {
    document.location.reload();
  }
}
