import type { RoleName } from '@shared/api/types';

export const ROLES: readonly { value: RoleName; label: string }[] = [
  { value: 'user', label: 'Пользователь' },
  { value: 'moderator', label: 'Модератор' },
  { value: 'admin', label: 'Администратор' },
];

export function roleLabel(role: RoleName): string {
  return ROLES.find((item) => item.value === role)?.label ?? role;
}
