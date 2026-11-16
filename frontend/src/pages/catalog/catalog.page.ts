import { Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { SessionService } from '@entities/user';
import { CatalogCategories } from '@features/catalog-categories';
import { CatalogGroups } from '@features/catalog-groups';
import { CatalogObjectTypes } from '@features/catalog-object-types';
import { CatalogStatuses } from '@features/catalog-statuses';
import { P } from '@shared/lib/permissions';
import type { Permission } from '@shared/lib/permissions';
import { Icon } from '@shared/ui/icon';
import { PageHead } from '@shared/ui/page-head';
import { TabItem, Tabs } from '@shared/ui/tabs';

interface CatalogTab extends TabItem {
  permission: Permission;
}

const TABS: CatalogTab[] = [
  { id: 'categories', label: 'Категории', icon: 'book', permission: P.categoryManage },
  { id: 'types', label: 'Типы объектов', icon: 'polygon', permission: P.objectTypeManage },
  { id: 'groups', label: 'Группы', icon: 'layers', permission: P.groupManage },
  { id: 'statuses', label: 'Справочник статусов', icon: 'palette', permission: P.statusManage },
];

@Component({
  selector: 'app-catalog-page',
  imports: [PageHead, Tabs, Icon, CatalogCategories, CatalogObjectTypes, CatalogGroups, CatalogStatuses],
  template: `
    <div class="page">
      <app-page-head title="Каталог" lead="Справочники, из которых собираются фиксации и источники. Только нужные вкладки — по вашим правам." />

      @if (tabs().length) {
        <app-tabs [tabs]="tabs()" [active]="active()" (activeChange)="select($event)" />

        @switch (active()) {
          @case ('categories') { <app-catalog-categories /> }
          @case ('types') { <app-catalog-object-types /> }
          @case ('groups') { <app-catalog-groups /> }
          @case ('statuses') { <app-catalog-statuses /> }
        }
      } @else {
        <p class="callout"><app-icon name="lock" /><span>Нет прав на управление каталогом.</span></p>
      }
    </div>`,
})
export class CatalogPage {
  private readonly session = inject(SessionService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  private readonly requested = signal(this.route.snapshot.queryParamMap.get('tab') ?? '');

  protected readonly tabs = computed<TabItem[]>(() =>
    TABS.filter((tab) => this.session.has(tab.permission)).map(({ id, label, icon }) => ({ id, label, icon })),
  );

  protected readonly active = computed(() => {
    const available = this.tabs();
    const requested = this.requested();
    return available.some((tab) => tab.id === requested) ? requested : (available[0]?.id ?? '');
  });

  protected select(tab: string): void {
    if (tab === this.active()) return;
    this.requested.set(tab);
    void this.router.navigate([], { relativeTo: this.route, queryParams: { tab }, queryParamsHandling: 'merge', replaceUrl: true });
  }
}
