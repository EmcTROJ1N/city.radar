import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { ApiClient } from '@shared/api/api-client';
import { AuthTokenStore } from '@shared/api/auth-token';
import type { Me, TokenResponse } from '@shared/api/types';
import { Permission } from '@shared/lib/permissions';

const ROLE_LABEL: Record<string, string> = {
  user: 'Пользователь',
  moderator: 'Модератор',
  admin: 'Администратор',
};

interface DevLoginRequest {
  login: string;
  password: string;
}

@Injectable({ providedIn: 'root' })
export class SessionService {
  private readonly api = inject(ApiClient);
  private readonly tokens = inject(AuthTokenStore);

  readonly me = signal<Me | null>(null);
  readonly ready = signal(false);
  readonly isAuthenticated = computed(() => this.me() !== null);
  readonly permissions = computed(() => new Set(this.me()?.permissions ?? []));
  readonly roleLabel = computed(() => {
    const role = this.me()?.roles[0];
    if (!role) return 'Гость';
    return ROLE_LABEL[role] ?? role;
  });

  private initializing: Promise<void> | null = null;

  constructor() {
    // Потеря access-токена (например, неудачный refresh в interceptor) означает выход.
    effect(() => {
      if (this.tokens.token() === null) untracked(() => this.me.set(null));
    });
  }

  has(permission: Permission): boolean {
    return this.permissions().has(permission);
  }

  hasAny(permissions: readonly Permission[]): boolean {
    const own = this.permissions();
    return permissions.some((p) => own.has(p));
  }

  init(): Promise<void> {
    if (this.ready()) return Promise.resolve();
    if (!this.initializing) {
      this.initializing = this.doInit().finally(() => {
        this.initializing = null;
      });
    }
    return this.initializing;
  }

  async loadMe(): Promise<void> {
    this.me.set(await this.api.get<Me>('/me'));
  }

  async devLogin(login: string, password: string): Promise<void> {
    const body: DevLoginRequest = { login, password };
    const { accessToken } = await this.api.post<TokenResponse>('/auth/dev-login', body);
    this.tokens.set(accessToken);
    try {
      await this.loadMe();
    } catch (err) {
      this.tokens.clear();
      this.me.set(null);
      throw err;
    }
  }

  async logout(): Promise<void> {
    try {
      await this.api.post('/auth/logout');
    } catch {
      // Выход локально должен произойти в любом случае.
    }
    this.tokens.clear();
    this.me.set(null);
  }

  private async doInit(): Promise<void> {
    try {
      if (await this.tokens.refresh()) {
        await this.loadMe();
      }
    } catch {
      // Нет сессии — это нормальное состояние гостя.
    } finally {
      this.ready.set(true);
    }
  }
}
