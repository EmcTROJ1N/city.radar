export const P = {
  mapRead: 'map.read',
  observationCreate: 'observation.create',
  observationRetract: 'observation.retract',
  categoryManage: 'catalog.category.manage',
  objectTypeManage: 'catalog.objecttype.manage',
  objectManage: 'catalog.object.manage',
  groupManage: 'catalog.group.manage',
  statusManage: 'catalog.status.manage',
  sourceManage: 'source.manage',
  moderationProcess: 'moderation.queue.process',
  secretsManage: 'source.secrets.manage',
  settingsManage: 'settings.global.manage',
  rolesAssign: 'users.roles.assign',
  auditRead: 'audit.read',
} as const;

export type Permission = (typeof P)[keyof typeof P];

export const CATALOG_PERMISSIONS: readonly Permission[] = [
  P.categoryManage,
  P.objectTypeManage,
  P.objectManage,
  P.groupManage,
  P.statusManage,
];

export const ADMIN_PERMISSIONS: readonly Permission[] = [
  P.secretsManage,
  P.settingsManage,
  P.rolesAssign,
  P.auditRead,
];
