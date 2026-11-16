import { Injectable, inject } from '@angular/core';
import { ApiClient } from '@shared/api/api-client';
import type {
  AdminUser,
  AdminUserPage,
  ApiPaths,
  AuditPage,
  GlobalSettings,
  GlobalSettingsPatch,
  LlmSecret,
  ProxySecret,
  SecretMeta,
  SetRolesRequest,
  TelegramSessionSecret,
} from '@shared/api/types';

export type ListUsersQuery = NonNullable<ApiPaths['/admin/users']['get']['parameters']['query']>;
export type ListAuditQuery = NonNullable<ApiPaths['/admin/audit']['get']['parameters']['query']>;

@Injectable({ providedIn: 'root' })
export class AdminApi {
  private readonly api = inject(ApiClient);

  listUsers(query: ListUsersQuery = {}): Promise<AdminUserPage> {
    return this.api.get<AdminUserPage>('/admin/users', query);
  }

  setRoles(id: string, body: SetRolesRequest): Promise<AdminUser> {
    return this.api.put<AdminUser>(`/admin/users/${encodeURIComponent(id)}/roles`, body);
  }

  listAudit(query: ListAuditQuery = {}): Promise<AuditPage> {
    return this.api.get<AuditPage>('/admin/audit', query);
  }

  getSettings(): Promise<GlobalSettings> {
    return this.api.get<GlobalSettings>('/admin/settings');
  }

  updateSettings(body: GlobalSettingsPatch): Promise<GlobalSettings> {
    return this.api.put<GlobalSettings>('/admin/settings', body);
  }

  listSecrets(): Promise<SecretMeta[]> {
    return this.api.get<SecretMeta[]>('/admin/secrets');
  }

  putTelegramSession(id: string, body: TelegramSessionSecret): Promise<void> {
    return this.api.put<void>(`/admin/secrets/telegram-sessions/${encodeURIComponent(id)}`, body);
  }

  putProxy(id: string, body: ProxySecret): Promise<void> {
    return this.api.put<void>(`/admin/secrets/proxies/${encodeURIComponent(id)}`, body);
  }

  putLlm(body: LlmSecret): Promise<void> {
    return this.api.put<void>('/admin/secrets/llm', body);
  }
}
