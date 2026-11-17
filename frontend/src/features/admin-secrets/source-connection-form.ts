import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { SourcesApi } from '@entities/source';
import { ApiError, toApiError } from '@shared/api/api-error';
import type { SecretMeta, SourceConnection, SourceType, TelegramMode } from '@shared/api/types';
import {
  SOURCE_TYPE_LABEL,
  TELEGRAM_MODE_HINT,
  TELEGRAM_MODE_LABEL,
} from '@shared/lib/labels';
import { EntityPicker, PickerLoader } from '@shared/ui/entity-picker';
import { FieldError } from '@shared/ui/field-error';
import { FormError } from '@shared/ui/form-error';
import { Icon } from '@shared/ui/icon';
import { ToastService } from '@shared/ui/toast';

/**
 * Какие Telegram-сессия и прокси назначены источнику. Список сессий фильтруется по способу
 * чтения источника: боту — токены Bot API, MTProto — аккаунты.
 */
@Component({
  selector: 'app-source-connection-form',
  imports: [EntityPicker, FieldError, FormError, Icon],
  template: `
    <form class="card stack" (submit)="save($event)">
      <h2>Подключение источника</h2>
      <p class="hint">Бот или аккаунт и прокси, которые использует источник. При заданном прокси прямое соединение запрещено.</p>

      <div class="field">
        <span>Источник</span>
        <app-entity-picker [loader]="loadSources" [(value)]="sourceIds" placeholder="Выберите источник" />
      </div>

      @if (sourceIds().length) {
        @if (isTelegram()) {
          <div class="callout callout--info">
            <app-icon name="info" />
            <span>
              Способ чтения источника: <strong>{{ modeLabel() }}</strong>.
              {{ modeHint() }}
            </span>
          </div>
        }

        <div class="row">
          @if (isTelegram()) {
            <label class="field grow">
              <span>{{ sessionLabel() }}</span>
              <select [value]="sessionId()" (change)="sessionId.set($any($event.target).value)">
                <option value="" [selected]="sessionId() === ''">Не задано</option>
                @for (item of availableSessions(); track item.id) {
                  <option [value]="item.id" [selected]="sessionId() === item.id">{{ item.name }}</option>
                }
              </select>
              @if (!availableSessions().length) {
                <span class="hint">
                  Нет сохранённых подключений этого способа. Создайте его в блоке
                  «Telegram-подключение» ниже на странице.
                </span>
              }
              <app-field-error [error]="error()" field="telegramSessionId" />
            </label>
          } @else {
            <p class="hint grow">Для источника этого типа Telegram-сессия не используется.</p>
          }

          <label class="field grow">
            <span>Прокси</span>
            <select [value]="proxyId()" (change)="proxyId.set($any($event.target).value)">
              <option value="" [selected]="proxyId() === ''">Не задано</option>
              @for (item of proxies(); track item.id) {
                <option [value]="item.id" [selected]="proxyId() === item.id">{{ item.name }}</option>
              }
            </select>
            <app-field-error [error]="error()" field="proxyId" />
          </label>
        </div>

        <app-form-error [error]="error()" [fields]="['telegramSessionId', 'proxyId']" />

        <div class="row">
          <button class="btn btn--primary" type="submit" [disabled]="loading() || busy()">
            {{ busy() ? 'Сохранение…' : 'Сохранить подключение' }}
          </button>
        </div>
      } @else {
        <p class="empty">Выберите источник, чтобы увидеть назначения.</p>
      }
    </form>`,
})
export class SourceConnectionForm {
  private readonly sources = inject(SourcesApi);
  private readonly toasts = inject(ToastService);

  readonly sessions = input<readonly SecretMeta[]>([]);
  readonly proxies = input<readonly SecretMeta[]>([]);

  protected readonly sourceIds = signal<string[]>([]);
  protected readonly sourceType = signal<SourceType | null>(null);
  protected readonly sourceMode = signal<TelegramMode | null>(null);
  protected readonly sessionId = signal('');
  protected readonly proxyId = signal('');
  protected readonly loading = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal<ApiError | null>(null);

  protected readonly isTelegram = computed(() => this.sourceType() === 'telegram_chat');

  protected readonly modeLabel = computed(() => {
    const mode = this.sourceMode();
    if (!mode) return '';
    return TELEGRAM_MODE_LABEL[mode];
  });

  protected readonly modeHint = computed(() => {
    const mode = this.sourceMode();
    return mode ? TELEGRAM_MODE_HINT[mode] : '';
  });

  protected readonly sessionLabel = computed(() =>
    this.sourceMode() === 'userbot' ? 'Сессия MTProto' : 'Telegram-бот',
  );

  /** Только секреты того же способа, что и у источника. */
  protected readonly availableSessions = computed(() => {
    const mode = this.sourceMode() ?? 'bot';
    return this.sessions().filter((item) => (item.mode ?? 'bot') === mode);
  });

  private token = 0;

  protected readonly loadSources: PickerLoader = async (q, cursor) => {
    const page = await this.sources.list({
      limit: 20,
      cursor: cursor ?? undefined,
      q: q || undefined,
    });
    return {
      items: page.items.map((source) => ({
        value: source.id,
        label: source.name,
        hint: source.mode
          ? `${SOURCE_TYPE_LABEL[source.sourceType]} · ${TELEGRAM_MODE_LABEL[source.mode]}`
          : SOURCE_TYPE_LABEL[source.sourceType],
      })),
      nextCursor: page.nextCursor,
    };
  };

  constructor() {
    effect(() => {
      const id = this.sourceIds()[0];
      untracked(() => void this.load(id));
    });
  }

  protected async save(event: Event): Promise<void> {
    event.preventDefault();
    const id = this.sourceIds()[0];
    if (!id || this.busy() || this.loading()) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.sources.updateConnection(id, this.body());
      this.toasts.success('Подключение источника сохранено');
    } catch (err) {
      this.error.set(toApiError(err));
      this.toasts.fromError(err);
    } finally {
      this.busy.set(false);
    }
  }

  private body(): SourceConnection {
    return {
      telegramSessionId: this.isTelegram() ? this.sessionId() || null : null,
      proxyId: this.proxyId() || null,
    };
  }

  private async load(id: string | undefined): Promise<void> {
    const token = ++this.token;
    this.error.set(null);
    if (!id) {
      this.reset();
      this.loading.set(false);
      return;
    }
    this.loading.set(true);
    this.reset();
    try {
      const [source, connection] = await Promise.all([
        this.sources.get(id),
        this.sources.getConnection(id),
      ]);
      if (token !== this.token) return;

      this.sourceType.set(source.sourceType);
      const mode = source.mode ?? (source.sourceType === 'telegram_chat' ? 'bot' : null);
      this.sourceMode.set(mode);

      // Старая привязка другого способа не должна остаться в форме: бэкенд её отклонит.
      const assigned = connection.telegramSessionId ?? '';
      const allowed = this.sessions().filter((item) => (item.mode ?? 'bot') === mode);
      this.sessionId.set(allowed.some((item) => item.id === assigned) ? assigned : '');
      this.proxyId.set(connection.proxyId ?? '');
    } catch (err) {
      if (token !== this.token) return;
      this.error.set(toApiError(err));
      this.toasts.fromError(err);
    } finally {
      if (token === this.token) this.loading.set(false);
    }
  }

  private reset(): void {
    this.sourceType.set(null);
    this.sourceMode.set(null);
    this.sessionId.set('');
    this.proxyId.set('');
  }
}
