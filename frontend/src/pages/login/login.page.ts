import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { SessionService } from '@entities/user';
import { ApiClient } from '@shared/api/api-client';
import { errorText, toApiError } from '@shared/api/api-error';
import { environment } from '@env/environment';
import { Icon } from '@shared/ui/icon';
import { PageHead } from '@shared/ui/page-head';
import { ToastService } from '@shared/ui/toast';

@Component({
  selector: 'app-login-page',
  imports: [FormsModule, Icon, PageHead],
  template: `
    <div class="page narrow">
      <app-page-head title="Вход" lead="Единственный способ входа в продукт — мессенджер MAX." />

      <p class="callout" role="alert">
        <app-icon name="alert" />
        <span><strong>Авторизация через MAX будет доступна позже.</strong></span>
      </p>

      @if (session.me(); as me) {
        <section class="card stack" aria-labelledby="who-title">
          <div>
            <h2 id="who-title">Текущая сессия</h2>
            <p>Вы вошли как <strong>{{ me.displayName }}</strong> ({{ session.roleLabel() }}).</p>
          </div>
          <div class="row">
            @for (p of me.permissions; track p) { <span class="chip">{{ p }}</span> } @empty {
              <span class="hint">Права не назначены.</span>
            }
          </div>
          <div class="row">
            <button class="btn" type="button" (click)="logout()"><app-icon name="logout" />Выйти</button>
          </div>
        </section>
      }

      <div class="card stack">
        <button class="btn btn--primary" type="button" [disabled]="busy()" (click)="loginByMax()">
          <app-icon name="message" />Войти через MAX
        </button>
        @if (maxError()) {
          <p class="callout" role="alert"><app-icon name="alert" /><span>{{ maxError() }}</span></p>
        }
      </div>

      @if (devAuth) {
        <section class="card stack" aria-labelledby="dev-title">
          <div>
            <h2 id="dev-title">Вход для разработки</h2>
          </div>
          <form class="stack" (ngSubmit)="devLogin()">
            <div class="field">
              <label for="dev-login">Логин</label>
              <input id="dev-login" name="dev-login" type="text" autocomplete="username" spellcheck="false"
                [ngModel]="devLoginName()" (ngModelChange)="devLoginName.set($event)" />
            </div>
            <div class="field">
              <label for="dev-password">Пароль</label>
              <input id="dev-password" name="dev-password" type="password" autocomplete="current-password"
                [ngModel]="devPassword()" (ngModelChange)="devPassword.set($event)" />
            </div>
            <div class="row">
              <button class="btn btn--primary" type="submit" [disabled]="!devLoginName().trim() || !devPassword() || busy()">Войти</button>
            </div>
          </form>
          @if (devError()) {
            <p class="callout" role="alert"><app-icon name="alert" /><span>{{ devError() }}</span></p>
          }
          <p class="hint">Логин — MAX ID пользователя из базы (например, 900000005) или admin / moderator / user. Пароль — DevAuth:Password из appsettings.Development.json.</p>
        </section>
      }
    </div>`,
  styles: `
    .narrow { max-width: 640px; }
    .hint { font-size: 12px; color: var(--ink-3); }
  `,
})
export class LoginPage {
  protected readonly session = inject(SessionService);
  private readonly api = inject(ApiClient);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly toast = inject(ToastService);

  protected readonly devAuth = environment.devAuth;
  protected readonly devLoginName = signal('');
  protected readonly devPassword = signal('');
  protected readonly devError = signal('');
  protected readonly maxError = signal('');
  protected readonly busy = signal(false);

  protected async loginByMax(): Promise<void> {
    this.maxError.set('');
    this.busy.set(true);
    try {
      await this.api.post('/auth/max');
      await this.session.loadMe();
      this.toast.success('Вход выполнен');
      await this.goBack();
    } catch (err) {
      this.maxError.set(errorText(toApiError(err)));
    } finally {
      this.busy.set(false);
    }
  }

  protected async devLogin(): Promise<void> {
    this.devError.set('');
    this.busy.set(true);
    try {
      await this.session.devLogin(this.devLoginName().trim(), this.devPassword());
      this.toast.success('Вход выполнен');
      await this.goBack();
    } catch (err) {
      this.devError.set(errorText(toApiError(err)));
    } finally {
      this.busy.set(false);
    }
  }

  protected async logout(): Promise<void> {
    await this.session.logout();
    this.toast.success('Вы вышли');
  }

  private async goBack(): Promise<void> {
    await this.router.navigateByUrl(this.returnUrl());
  }

  private returnUrl(): string {
    const raw = this.route.snapshot.queryParamMap.get('returnUrl');
    return raw && raw.startsWith('/') && !raw.startsWith('//') ? raw : '/';
  }
}
