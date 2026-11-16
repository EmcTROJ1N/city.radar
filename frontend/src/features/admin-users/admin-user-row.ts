import { Component, computed, inject, input, output, signal } from '@angular/core';
import { AdminApi } from '@entities/user';
import { toApiError } from '@shared/api/api-error';
import type { AdminUser, RoleName } from '@shared/api/types';
import { dateTime, agoIso } from '@shared/lib/format';
import { ConfirmService } from '@shared/ui/confirm';
import { Hint } from '@shared/ui/hint';
import { ToastService } from '@shared/ui/toast';
import { ROLES, roleLabel } from './roles';

/** Строка списка пользователей: роли меняются набором из трёх ролей. */
@Component({
  selector: 'tr[app-admin-user-row]',
  imports: [Hint],
  template: `
    <td>
      <strong>{{ user().displayName }}</strong>
      @if (isSelf()) { <span class="chip">это вы</span> }
    </td>
    <td class="num">{{ user().maxUserId }}</td>
    <td>
      <div class="row">
        @for (role of user().roles; track role) { <span class="chip chip--brand">{{ label(role) }}</span> }
      </div>
    </td>
    <td>{{ created() }}</td>
    <td>{{ lastLogin() }}</td>
    <td>
      <div class="roles">
        @for (role of roles; track role.value) {
          <label class="role">
            <input
              type="checkbox"
              [checked]="isChecked(role.value)"
              [disabled]="isLocked(role.value) || busy()"
              (change)="toggle(role.value)"
            />
            <span>{{ role.label }}</span>
            @if (isLocked(role.value)) { <app-hint text="Нельзя снять роль администратора у себя" /> }
          </label>
        }
        @if (changed()) {
          <button class="btn btn--sm btn--primary" type="button" [disabled]="busy()" (click)="save()">
            {{ busy() ? 'Сохранение…' : 'Сохранить' }}
          </button>
        }
      </div>
      @if (error(); as message) { <p class="err" role="alert">{{ message }}</p> }
    </td>`,
  styles: `
    .roles { display: flex; flex-wrap: wrap; gap: 6px 12px; align-items: center; }
    .role { display: inline-flex; align-items: center; gap: 6px; white-space: nowrap; }
    .role input { margin: 0; }
    .err { margin-top: 6px; font-size: 12px; color: var(--st-none); }
  `,
})
export class AdminUserRow {
  private readonly api = inject(AdminApi);
  private readonly confirm = inject(ConfirmService);
  private readonly toasts = inject(ToastService);

  readonly user = input.required<AdminUser>();
  readonly isSelf = input(false);
  readonly saved = output<AdminUser>();

  protected readonly roles = ROLES;
  protected readonly label = roleLabel;
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  private readonly draft = signal<RoleName[] | null>(null);
  protected readonly selected = computed(() => this.draft() ?? this.user().roles);

  protected readonly changed = computed(() => {
    const current = [...this.selected()].sort().join(',');
    const initial = [...this.user().roles].sort().join(',');
    return current !== initial;
  });

  protected readonly created = computed(() => dateTime(this.user().createdAt));
  protected readonly lastLogin = computed(() => {
    const at = this.user().lastLoginAt;
    return at ? agoIso(at) : 'не входил';
  });

  protected isChecked(role: RoleName): boolean {
    return this.selected().includes(role);
  }

  protected isLocked(role: RoleName): boolean {
    return role === 'admin' && this.isSelf();
  }

  protected toggle(role: RoleName): void {
    if (this.isLocked(role)) return;
    const current = this.selected();
    this.draft.set(
      current.includes(role) ? current.filter((item) => item !== role) : [...current, role],
    );
  }

  protected async save(): Promise<void> {
    if (!this.changed() || this.busy()) return;
    const before = this.user().roles;
    const next = this.selected();
    const adminWas = before.includes('admin');
    const adminWill = next.includes('admin');

    if (adminWas !== adminWill) {
      const grant = adminWill;
      const answer = await this.confirm.ask({
        title: grant ? 'Выдать роль администратора?' : 'Снять роль администратора?',
        text: grant
          ? `${this.user().displayName} получит полный доступ к администрированию.`
          : `${this.user().displayName} потеряет доступ к администрированию.`,
        confirmLabel: grant ? 'Выдать' : 'Снять',
        danger: !grant,
      });
      if (!answer.confirmed) return;
    }

    this.busy.set(true);
    this.error.set(null);
    try {
      const order = ROLES.map((role) => role.value);
      const updated = await this.api.setRoles(this.user().id, {
        roles: order.filter((role) => next.includes(role)),
      });
      this.draft.set(null);
      this.saved.emit(updated);
      this.toasts.success('Роли сохранены');
    } catch (err) {
      const apiError = toApiError(err);
      this.error.set(apiError.detail ?? apiError.title);
      this.toasts.fromError(apiError);
    } finally {
      this.busy.set(false);
    }
  }
}
