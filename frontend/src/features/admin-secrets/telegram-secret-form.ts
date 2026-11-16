import { Component, computed, inject, input, output, signal } from '@angular/core';
import { AdminApi } from '@entities/user';
import { ApiError, toApiError } from '@shared/api/api-error';
import type { SecretMeta } from '@shared/api/types';
import { FieldError } from '@shared/ui/field-error';
import { FormError } from '@shared/ui/form-error';
import { ToastService } from '@shared/ui/toast';

/** Токен Telegram-бота: цифры, двоеточие и буквенно-цифровой хвост от BotFather. */
const BOT_TOKEN_PATTERN = /^\d{5,}:[A-Za-z0-9_-]{20,}$/;

/**
 * Токен Telegram-бота (Bot API). В первой версии UI поддерживает только бота:
 * юзербот скрыт, поэтому здесь нет ни JSON-сессии, ни выбора режима.
 */
@Component({
  selector: 'app-telegram-secret-form',
  imports: [FieldError, FormError],
  template: `
    <form class="card stack" (submit)="save($event)">
      <h2>Telegram-бот</h2>
      <p class="hint">
        Бот читает сообщения из чата и передаёт их на разбор. В этой версии мониторинг работает
        только через Telegram-бота.
      </p>

      @if (sessions().length) {
        <label class="field">
          <span>Что заменить</span>
          <select id="tg-target" [value]="target()" (change)="pickTarget($any($event.target).value)">
            <option value="" [selected]="target() === ''">Новый бот</option>
            @for (item of sessions(); track item.id) {
              <option [value]="item.id" [selected]="target() === item.id">{{ item.name }}</option>
            }
          </select>
        </label>
      }

      <label class="field">
        <span>Название</span>
        <input
          id="tg-name"
          [value]="name()"
          (input)="name.set($any($event.target).value)"
          autocomplete="off"
          placeholder="Например, Бот района"
        />
        <span class="hint">Так бот будет называться в списке и в подключении источника.</span>
        <app-field-error [error]="error()" field="name" />
      </label>

      <label class="field">
        <span>Токен бота</span>
        <input
          id="tg-token"
          type="text"
          autocomplete="off"
          spellcheck="false"
          [value]="token()"
          (input)="token.set($any($event.target).value)"
          placeholder="123456789:AAH..."
        />
        <span class="hint">
          Создайте бота у &#64;BotFather, скопируйте токен и добавьте бота в чат.
          Токен сохраняется зашифрованным и больше не показывается.
        </span>
        @if (tokenError(); as message) { <span class="err">{{ message }}</span> }
        <app-field-error [error]="error()" field="session" />
      </label>

      <label class="field">
        <span>Прокси (необязательно)</span>
        <select id="tg-proxy" [value]="proxyId()" (change)="proxyId.set($any($event.target).value)">
          <option value="" [selected]="proxyId() === ''">Без прокси</option>
          @for (proxy of proxies(); track proxy.id) {
            <option [value]="proxy.id" [selected]="proxyId() === proxy.id">{{ proxy.name }}</option>
          }
        </select>
        <span class="hint">Для Bot API подходит SOCKS5. MTProxy — только для MTProto и здесь не сработает.</span>
        <app-field-error [error]="error()" field="proxyId" />
      </label>

      <p class="hint">
        Перед сохранением токен проверяется запросом к Telegram; если прокси задан — только через него.
        После сохранения назначьте бота источнику в блоке «Подключение источника» ниже.
      </p>

      <app-form-error [error]="error()" [fields]="['name', 'session', 'proxyId']" />

      <div class="row">
        <button class="btn btn--primary" type="submit" [disabled]="!valid() || busy()">
          {{ busy() ? 'Сохранение…' : 'Сохранить токен' }}
        </button>
      </div>
    </form>`,
})
export class TelegramSecretForm {
  private readonly api = inject(AdminApi);
  private readonly toasts = inject(ToastService);

  readonly sessions = input<readonly SecretMeta[]>([]);
  readonly proxies = input<readonly SecretMeta[]>([]);
  readonly saved = output<void>();

  protected readonly target = signal('');
  protected readonly name = signal('');
  protected readonly token = signal('');
  protected readonly proxyId = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal<ApiError | null>(null);

  /** Понятная ошибка формата прямо в форме, без ожидания ответа сервера. */
  protected readonly tokenError = computed(() => {
    const value = this.token().trim();
    if (!value) return null;
    return BOT_TOKEN_PATTERN.test(value)
      ? null
      : 'Токен должен выглядеть так: 123456789:AAH... Скопируйте его у @BotFather.';
  });

  protected readonly valid = computed(
    () => Boolean(this.name().trim() && this.token().trim()) && this.tokenError() === null,
  );

  private pendingId = crypto.randomUUID();

  protected pickTarget(id: string): void {
    this.target.set(id);
    const meta = this.sessions().find((item) => item.id === id);
    this.name.set(meta?.name ?? '');
    this.proxyId.set(meta?.proxyId ?? '');
    this.token.set('');
  }

  protected async save(event: Event): Promise<void> {
    event.preventDefault();
    if (!this.valid() || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const id = this.target() || this.pendingId;
      await this.api.putTelegramSession(id, {
        name: this.name().trim(),
        session: this.token().trim(),
        proxyId: this.proxyId() || null,
      });
      this.toasts.success('Токен бота сохранён');
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
    this.target.set('');
    this.name.set('');
    this.token.set('');
    this.proxyId.set('');
    this.pendingId = crypto.randomUUID();
  }
}
