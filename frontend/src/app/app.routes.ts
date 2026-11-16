import { Routes } from '@angular/router';
import { permissionGuard } from '@entities/user';
import { ADMIN_PERMISSIONS, CATALOG_PERMISSIONS, P } from '@shared/lib/permissions';

export const routes: Routes = [
  { path: '', pathMatch: 'full', title: 'Карта', loadComponent: () => import('@pages/map/map.page').then((m) => m.MapPage) },
  { path: 'login', title: 'Вход', loadComponent: () => import('@pages/login/login.page').then((m) => m.LoginPage) },
  { path: 'sources', title: 'Источники данных', canActivate: [permissionGuard(P.sourceManage)],
    loadComponent: () => import('@pages/sources/sources.page').then((m) => m.SourcesPage) },
  { path: 'map-objects', title: 'Фиксации на карте', canActivate: [permissionGuard(P.objectManage)],
    loadComponent: () => import('@pages/map-objects/map-objects.page').then((m) => m.MapObjectsPage) },
  { path: 'catalog', title: 'Каталог', canActivate: [permissionGuard(...CATALOG_PERMISSIONS)],
    loadComponent: () => import('@pages/catalog/catalog.page').then((m) => m.CatalogPage) },
  { path: 'moderation', title: 'Модерация', canActivate: [permissionGuard(P.moderationProcess)],
    loadComponent: () => import('@pages/moderation/moderation.page').then((m) => m.ModerationPage) },
  { path: 'admin', title: 'Администрирование', canActivate: [permissionGuard(...ADMIN_PERMISSIONS)],
    loadComponent: () => import('@pages/admin/admin.page').then((m) => m.AdminPage) },
  { path: '**', redirectTo: '' },
];
