import { Component, computed, inject, output, signal } from '@angular/core';
import { AdminApi } from '@entities/user';
import { ApiError, toApiError } from '@shared/api/api-error';
import type { ProxySecret } from '@shared/api/types';
import { FieldError } from '@shared/ui/field-error';
import { FormError } from '@shared/ui/form-error';
import { ToastService } from '@shared/ui/toast';

/** Новый прокси; пароль передаётся только когда заполнен и не остаётся в форме. */
@Component({
  selector: 'app-proxy-secret-form',
  imports: [FieldError, FormError],
  template: `
    <form class="card stack" (submit)="save($event)">
      <h2>Прокси</h2>
      <p class="hint">Перед сохранением проверяем TCP-доступность хоста и порта прокси.</p>

      <label class="field">
        <span>Название</span>
        <input id="px-name" [value]="name()" (input)="name.set($any($event.target).value)" autocomplete="off" />
        <app-field-error [error]="error()" field="name" />
      </label>

      <div class="row">
        <label class="field grow">
          <span>Схема</span>
          <select id="px-scheme" [value]="scheme()" (change)="scheme.set($any($event.target).value)">
            <option value="socks5" [selected]="scheme() === 'socks5'">socks5</option>
            <option value="mtproxy" [selected]="scheme() === 'mtproxy'">mtproxy</option>
          </select>
          <app-field-error [error]="error()" field="scheme" />
        </label>
        <label class="field grow">
          <span>Порт</span>
          <input id="px-port" type="number" min="1" max="65535" [value]="port()" (input)="port.set($any($event.target).value)" />
          <app-field-error [error]="error()" field="port" />
        </label>
      </div>

      <label class="field">
        <span>Хост</span>
        <input id="px-host" [value]="host()" (input)="host.set($any($event.target).value)" autocomplete="off" />
        <app-field-error [error]="error()" field="host" />
      </label>

      <div class="row">
        <label class="field grow">
          <span>Пользователь</span>
          <input id="px-username" [value]="username()" (input)="username.set($any($event.target).value)" autocomplete="off" />
          <app-field-error [error]="error()" field="username" />
        </label>
        <label class="field grow">
          <span>Пароль</span>
          <input
            id="px-password"
            type="password"
            autocomplete="off"
            [value]="password()"
            (input)="password.set($any($event.target).value)"
          />
          <span class="hint">Отправляется, только если заполнен.</span>
          <app-field-error [error]="error()" field="password" />
        </label>
      </div>

      <app-form-error [error]="error()" [fields]="['name', 'scheme', 'host', 'port', 'username', 'password']" />

      <div class="row">
        <button class="btn btn--primary" type="submit" [disabled]="!valid() || busy()">
          {{ busy() ? 'Сохранение…' : 'Сохранить прокси' }}
        </button>
      </div>
    </form>`,
})
export class ProxySecretForm {
  private readonly api = inject(AdminApi);
  private readonly toasts = inject(ToastService);

  readonly saved = output<void>();

  protected readonly name = signal('');
  protected readonly scheme = signal<ProxySecret['scheme']>('socks5');
  protected readonly host = signal('');
  protected readonly port = signal('1080');
  protected readonly username = signal('');
  protected readonly password = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal<ApiError | null>(null);

  protected readonly valid = computed(() => {
    const port = Number(this.port());
    return Boolean(this.name().trim() && this.host().trim()) && Number.isInteger(port) && port >= 1 && port <= 65535;
  });

  protected async save(event: Event): Promise<void> {
    event.preventDefault();
    if (!this.valid() || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const body: ProxySecret = {
        name: this.name().trim(),
        scheme: this.scheme(),
        host: this.host().trim(),
        port: Number(this.port()),
      };
      const username = this.username().trim();
      if (username) body.username = username;
      if (this.password()) body.password = this.password();

      await this.api.putProxy(crypto.randomUUID(), body);
      this.toasts.success('Прокси сохранён');
      this.reset();
      this.saved.emit();
    } catch (err) {
      this.error.set(toApiError(err));
      this.toasts.fromError(err);
    } finally {
      this.busy.set(false);
    }
  }

  private reset(): void {
    this.name.set('');
    this.scheme.set('socks5');
    this.host.set('');
    this.port.set('1080');
    this.username.set('');
    this.password.set('');
  }
}
