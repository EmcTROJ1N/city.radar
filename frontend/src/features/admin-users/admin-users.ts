import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { AdminApi, SessionService } from '@entities/user';
import { toApiError } from '@shared/api/api-error';
import type { AdminUser, RoleName } from '@shared/api/types';
import { LoadMore } from '@shared/ui/load-more';
import { SearchInput } from '@shared/ui/search-input';
import { ToastService } from '@shared/ui/toast';
import { AdminUserRow } from './admin-user-row';
import { ROLES } from './roles';

const PAGE_SIZE = 50;

/** Пользователи и роли. */
@Component({
  selector: 'app-admin-users',
  imports: [AdminUserRow, LoadMore, SearchInput],
  template: `
    <div class="card stack">
      <div class="row filters">
        <app-search-input class="grow" [(value)]="q" placeholder="Поиск по имени" />
        <label class="field role-field">
          <span>Роль</span>
          <select [value]="role()" (change)="role.set($any($event.target).value)">
            <option value="">Все роли</option>
            @for (item of roles; track item.value) { <option [value]="item.value">{{ item.label }}</option> }
          </select>
        </label>
      </div>
    </div>

    <div class="card card--flush table-wrap table-wrap--cards">
      <table class="table table--cards">
        <thead>
          <tr>
            <th>Пользователь</th>
            <th class="num">MAX ID</th>
            <th>Роли</th>
            <th>Создан</th>
            <th>Последний вход</th>
            <th>Доступ</th>
          </tr>
        </thead>
        <tbody>
          @if (loading() && !items().length) {
            <tr><td colspan="6" class="empty">Загрузка…</td></tr>
          } @else if (error() && !items().length) {
            <tr><td colspan="6" class="empty">Не удалось загрузить пользователей.</td></tr>
          } @else {
            @for (user of items(); track user.id) {
              <tr app-admin-user-row [user]="user" [isSelf]="user.id === selfId()" (saved)="replace($event)"></tr>
            } @empty {
              <tr><td colspan="6" class="empty">Пользователи не найдены.</td></tr>
            }
          }
        </tbody>
      </table>
    </div>

    <app-load-more [hasMore]="nextCursor() !== null" [loading]="loading()" (more)="more()" />`,
  styles: `
    :host { display: grid; gap: var(--gap); }
    .filters { align-items: flex-end; }
    .role-field { min-width: 190px; }
    .role-field select { height: var(--control-h); padding: 0 10px; border: 1px solid var(--line);
      border-radius: var(--r-md); background: var(--surface); }
  `,
})
export class AdminUsers {
  private readonly api = inject(AdminApi);
  private readonly session = inject(SessionService);
  private readonly toasts = inject(ToastService);

  protected readonly roles = ROLES;
  protected readonly q = signal('');
  protected readonly role = signal<RoleName | ''>('');
  protected readonly items = signal<AdminUser[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly selfId = computed(() => this.session.me()?.id ?? '');

  private token = 0;

  constructor() {
    effect(() => {
      const q = this.q();
      const role = this.role();
      untracked(() => void this.reload(q, role));
    });
  }

  protected replace(user: AdminUser): void {
    this.items.update((list) => list.map((item) => (item.id === user.id ? user : item)));
  }

  protected async more(): Promise<void> {
    const cursor = this.nextCursor();
    if (!cursor || this.loading()) return;
    await this.load(cursor);
  }

  private async reload(q: string, role: RoleName | ''): Promise<void> {
    this.items.set([]);
    this.nextCursor.set(null);
    await this.load(null, q, role);
  }

  private async load(
    cursor: string | null,
    q = this.q(),
    role = this.role(),
  ): Promise<void> {
    const token = ++this.token;
    this.loading.set(true);
    this.error.set(null);
    try {
      const page = await this.api.listUsers({
        limit: PAGE_SIZE,
        cursor: cursor ?? undefined,
        q: q.trim() || undefined,
        role: role || undefined,
      });
      if (token !== this.token) return;
      this.items.update((list) => (cursor ? [...list, ...page.items] : page.items));
      this.nextCursor.set(page.nextCursor);
    } catch (err) {
      if (token !== this.token) return;
      this.error.set(toApiError(err).title);
      this.toasts.fromError(err);
    } finally {
      if (token === this.token) this.loading.set(false);
    }
  }
}
