import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { SessionService } from '@entities/user';
import { Permission, P } from '@shared/lib/permissions';
import { PageHead } from '@shared/ui/page-head';
import { TabItem, Tabs } from '@shared/ui/tabs';
import { AdminAudit } from '@features/admin-audit/admin-audit';
import { AdminSecrets } from '@features/admin-secrets/admin-secrets';
import { AdminSettings } from '@features/admin-settings/admin-settings';
import { AdminUsers } from '@features/admin-users/admin-users';

interface AdminTab extends TabItem {
  permission: Permission;
}

const TABS: readonly AdminTab[] = [
  { id: 'users', label: 'Пользователи и роли', icon: 'user', permission: P.rolesAssign },
  { id: 'secrets', label: 'Секреты', icon: 'lock', permission: P.secretsManage },
  { id: 'settings', label: 'Глобальные настройки', icon: 'settings', permission: P.settingsManage },
  { id: 'audit', label: 'Журнал аудита', icon: 'book', permission: P.auditRead },
];

@Component({
  selector: 'app-admin-page',
  imports: [AdminAudit, AdminSecrets, AdminSettings, AdminUsers, PageHead, Tabs],
  template: `
    <div class="page">
      <app-page-head title="Администрирование" lead="Доступ, секреты и глобальные настройки." />

      @if (tabs().length) {
        <app-tabs [tabs]="tabs()" [active]="active()" (activeChange)="tab.set($event)" />

        @switch (active()) {
          @case ('users') { <app-admin-users /> }
          @case ('secrets') { <app-admin-secrets /> }
          @case ('settings') { <app-admin-settings /> }
          @case ('audit') { <app-admin-audit /> }
        }
      } @else {
        <div class="card empty">Для этого раздела нужны права администратора.</div>
      }
    </div>`,
})
export class AdminPage {
  private readonly session = inject(SessionService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly tabs = computed<TabItem[]>(() =>
    TABS.filter((tab) => this.session.has(tab.permission)).map(({ id, label, icon }) => ({ id, label, icon })),
  );

  protected readonly tab = signal(this.route.snapshot.queryParamMap.get('tab') ?? 'users');

  protected readonly active = computed(() => {
    const available = this.tabs();
    const requested = this.tab();
    return available.some((tab) => tab.id === requested) ? requested : (available[0]?.id ?? '');
  });

  constructor() {
    // Вкладка живёт в адресе: ссылку ?tab=audit можно открыть напрямую.
    effect(() => {
      const tab = this.active();
      if (!tab) return;
      untracked(() => {
        void this.router.navigate([], {
          relativeTo: this.route,
          queryParams: { tab },
          queryParamsHandling: 'merge',
          replaceUrl: true,
        });
      });
    });
  }
}
