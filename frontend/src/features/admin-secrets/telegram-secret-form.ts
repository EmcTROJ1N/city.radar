import { Component, computed, inject, input, output, signal } from '@angular/core';
import { AdminApi } from '@entities/user';
import { ApiError, toApiError } from '@shared/api/api-error';
import type { SecretMeta, TelegramMode } from '@shared/api/types';
import { FieldError } from '@shared/ui/field-error';
import { FormError } from '@shared/ui/form-error';
import { Icon } from '@shared/ui/icon';
import { ToastService } from '@shared/ui/toast';

/** Токен Telegram-бота: цифры, двоеточие и буквенно-цифровой хвост от BotFather. */
const BOT_TOKEN_PATTERN = /^\d{5,}:[A-Za-z0-9_-]{20,}$/;

/** api_hash с my.telegram.org — длинная hex-строка. */
const API_HASH_PATTERN = /^[A-Fa-f0-9]{20,}$/;

/**
 * Telegram-секрет: бот (Bot API) или аккаунт (MTProto). Способы разделены явным
 * переключателем, а поля ввода не пересекаются: токен бота либо JSON-сессия.
 */
@Component({
  selector: 'app-telegram-secret-form',
  imports: [FieldError, FormError, Icon],
  template: `
    <form class="card stack" (submit)="save($event)">
      <h2>Telegram-подключение</h2>
      <p class="hint">
        Выберите способ чтения. Бот подходит для своих чатов, аккаунт MTProto — для сторонних
        групп, в которых состоит аккаунт.
      </p>

      <div class="mode-grid" role="radiogroup" aria-label="Способ подключения Telegram">
        <button
          type="button"
          role="radio"
          class="mode-option"
          [class.is-active]="mode() === 'bot'"
          [attr.aria-checked]="mode() === 'bot'"
          (click)="selectMode('bot')"
        >
          <app-icon name="message" />
          <strong>Telegram-бот</strong>
          <span>Bot API · свои чаты</span>
        </button>
        <button
          type="button"
          role="radio"
          class="mode-option"
          [class.is-active]="mode() === 'userbot'"
          [attr.aria-checked]="mode() === 'userbot'"
          (click)="selectMode('userbot')"
        >
          <app-icon name="user" />
          <strong>Аккаунт Telegram</strong>
          <span>MTProto · сторонние группы</span>
        </button>
      </div>
      <app-field-error [error]="error()" field="mode" />

      @if (availableTargets().length) {
        <label class="field">
          <span>{{ mode() === 'bot' ? 'Что заменить' : 'Какую сессию заменить' }}</span>
          <select id="tg-target" [value]="target()" (change)="pickTarget($any($event.target).value)">
            <option value="" [selected]="target() === ''">Новый</option>
            @for (item of availableTargets(); track item.id) {
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
          [placeholder]="mode() === 'bot' ? 'Например, Бот района' : 'Например, Аккаунт района'"
        />
        <span class="hint">Так подключение будет называться в списке и при выборе источника.</span>
        <app-field-error [error]="error()" field="name" />
      </label>

      @if (mode() === 'bot') {
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
      } @else {
        <div class="row">
          <label class="field grow">
            <span>API ID</span>
            <input
              id="tg-api-id"
              type="text"
              inputmode="numeric"
              autocomplete="off"
              spellcheck="false"
              [value]="apiId()"
              (input)="apiId.set($any($event.target).value)"
              placeholder="1234567"
            />
            @if (apiIdError(); as message) { <span class="err">{{ message }}</span> }
          </label>
          <label class="field grow">
            <span>API Hash</span>
            <input
              id="tg-api-hash"
              type="text"
              autocomplete="off"
              spellcheck="false"
              [value]="apiHash()"
              (input)="apiHash.set($any($event.target).value)"
              placeholder="0123456789abcdef0123456789abcdef"
            />
            @if (apiHashError(); as message) { <span class="err">{{ message }}</span> }
          </label>
        </div>

        <label class="field">
          <span>Строка сессии (sessionData)</span>
          <textarea
            id="tg-session-data"
            autocomplete="off"
            spellcheck="false"
            [value]="sessionData()"
            (input)="sessionData.set($any($event.target).value)"
            placeholder="1BAA... (строка сессии аккаунта)"
          ></textarea>
          <span class="hint">
            Значения api_id и api_hash возьмите на my.telegram.org. Строку сессии получите
            при входе в аккаунт (WTelegramClient / StringSession). Она сохраняется зашифрованной
            и больше не показывается.
          </span>
          @if (sessionDataError(); as message) { <span class="err">{{ message }}</span> }
          <app-field-error [error]="error()" field="session" />
        </label>

        <label class="field">
          <span>Телефон (необязательно)</span>
          <input
            id="tg-phone"
            type="tel"
            autocomplete="off"
            [value]="phone()"
            (input)="phone.set($any($event.target).value)"
            placeholder="+79990000000"
          />
          <span class="hint">Номер аккаунта для справки; на чтение сообщений не влияет.</span>
        </label>
      }

      <label class="field">
        <span>Прокси (необязательно)</span>
        <select id="tg-proxy" [value]="proxyId()" (change)="proxyId.set($any($event.target).value)">
          <option value="" [selected]="proxyId() === ''">Без прокси</option>
          @for (proxy of proxies(); track proxy.id) {
            <option [value]="proxy.id" [selected]="proxyId() === proxy.id">{{ proxy.name }}</option>
          }
        </select>
        <span class="hint">{{ proxyHint() }}</span>
        <app-field-error [error]="error()" field="proxyId" />
      </label>

      <p class="hint">
        Перед сохранением бот проверяется запросом к Telegram, сессия MTProto — по структуре.
        После сохранения назначьте подключение источнику в блоке «Подключение источника».
      </p>

      <app-form-error [error]="error()" [fields]="['name', 'session', 'mode', 'proxyId']" />

      <div class="row">
        <button class="btn btn--primary" type="submit" [disabled]="!valid() || busy()">
          {{ busy() ? 'Сохранение…' : saveLabel() }}
        </button>
      </div>
    </form>`,
  styles: `
    .mode-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 8px; }
    .mode-option {
      display: grid; gap: 3px; justify-items: start; padding: 10px 12px; text-align: left;
      background: var(--surface); border: 1px solid var(--line); border-radius: var(--r-md);
      color: var(--ink-2); cursor: pointer;
    }
    .mode-option strong { color: var(--ink); font-size: 14px; }
    .mode-option span { font-size: 12px; }
    .mode-option:hover { background: var(--surface-2); }
    .mode-option.is-active { border-color: var(--brand); background: var(--brand-soft); box-shadow: var(--focus-ring); }
    .mode-option app-icon { color: var(--brand); }
    .err { color: var(--st-none); font-size: 12px; }
  `,
})
export class TelegramSecretForm {
  private readonly api = inject(AdminApi);
  private readonly toasts = inject(ToastService);

  readonly sessions = input<readonly SecretMeta[]>([]);
  readonly proxies = input<readonly SecretMeta[]>([]);
  readonly saved = output<void>();

  protected readonly mode = signal<TelegramMode>('bot');
  protected readonly target = signal('');
  protected readonly name = signal('');
  protected readonly token = signal('');
  protected readonly apiId = signal('');
  protected readonly apiHash = signal('');
  protected readonly sessionData = signal('');
  protected readonly phone = signal('');
  protected readonly proxyId = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal<ApiError | null>(null);

  /** Существующие секреты только выбранного способа, чтобы не заменить чужой тип. */
  protected readonly availableTargets = computed(() =>
    this.sessions().filter((item) => (item.mode ?? 'bot') === this.mode()),
  );

  protected readonly saveLabel = computed(() =>
    this.mode() === 'bot' ? 'Сохранить бота' : 'Сохранить сессию',
  );

  protected readonly proxyHint = computed(() =>
    this.mode() === 'bot'
      ? 'Для Bot API подходит SOCKS5 или HTTP. MTProxy — только для MTProto.'
      : 'Для MTProto подходит SOCKS5 или MTProxy.',
  );

  protected readonly tokenError = computed(() => {
    const value = this.token().trim();
    if (!value) return null;
    return BOT_TOKEN_PATTERN.test(value)
      ? null
      : 'Токен должен выглядеть так: 123456789:AAH... Скопируйте его у @BotFather.';
  });

  protected readonly apiIdError = computed(() => {
    const value = this.apiId().trim();
    if (!value) return null;
    return /^\d+$/.test(value) && Number(value) > 0
      ? null
      : 'API ID — положительное число с my.telegram.org.';
  });

  protected readonly apiHashError = computed(() => {
    const value = this.apiHash().trim();
    if (!value) return null;
    return API_HASH_PATTERN.test(value)
      ? null
      : 'API Hash — hex-строка с my.telegram.org (обычно 32 символа).';
  });

  protected readonly sessionDataError = computed(() => {
    const value = this.sessionData().trim();
    if (!value) return null;
    return value.length < 20 ? 'Строка сессии выглядит слишком короткой.' : null;
  });

  protected readonly valid = computed(() => {
    if (!this.name().trim()) return false;

    return this.mode() === 'bot'
      ? Boolean(this.token().trim()) && this.tokenError() === null
      : this.apiIdError() === null
        && this.apiHashError() === null
        && this.sessionDataError() === null
        && Boolean(this.apiId().trim() && this.apiHash().trim() && this.sessionData().trim());
  });

  private pendingId = crypto.randomUUID();

  protected selectMode(mode: TelegramMode): void {
    this.mode.set(mode);

    // Скрытый секрет другого способа не должен остаться выбранным для замены.
    const current = this.target();
    if (current && !this.availableTargets().some((item) => item.id === current)) {
      this.target.set('');
    }
  }

  protected pickTarget(id: string): void {
    const meta = this.sessions().find((item) => item.id === id);
    this.target.set(id);
    this.mode.set(meta?.mode === 'userbot' ? 'userbot' : 'bot');
    this.name.set(meta?.name ?? '');
    this.proxyId.set(meta?.proxyId ?? '');
    this.clearSecretFields();
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
        mode: this.mode(),
        session: this.sessionValue(),
        proxyId: this.proxyId() || null,
      });
      this.toasts.success(this.mode() === 'bot' ? 'Бот сохранён' : 'Сессия MTProto сохранена');
      this.reset();
      this.saved.emit();
    } catch (err) {
      this.error.set(toApiError(err));
      this.toasts.fromError(err);
    } finally {
      this.busy.set(false);
    }
  }

  private sessionValue(): string {
    if (this.mode() === 'bot') return this.token().trim();

    const payload: Record<string, string | number> = {
      apiId: Number(this.apiId().trim()),
      apiHash: this.apiHash().trim(),
      sessionData: this.sessionData().trim(),
    };
    const phone = this.phone().trim();
    if (phone) payload['phone'] = phone;

    return JSON.stringify(payload);
  }

  private reset(): void {
    this.target.set('');
    this.name.set('');
    this.clearSecretFields();
    this.proxyId.set('');
    this.pendingId = crypto.randomUUID();
  }

  private clearSecretFields(): void {
    this.token.set('');
    this.apiId.set('');
    this.apiHash.set('');
    this.sessionData.set('');
    this.phone.set('');
  }
}
