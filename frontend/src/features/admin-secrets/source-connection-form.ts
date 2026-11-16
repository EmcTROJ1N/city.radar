import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { SourcesApi } from '@entities/source';
import { ApiError, toApiError } from '@shared/api/api-error';
import type { SecretMeta, SourceConnection } from '@shared/api/types';
import { SOURCE_TYPE_LABEL } from '@shared/lib/labels';
import { EntityPicker, PickerLoader } from '@shared/ui/entity-picker';
import { FieldError } from '@shared/ui/field-error';
import { FormError } from '@shared/ui/form-error';
import { ToastService } from '@shared/ui/toast';

/** Какие сессия Telegram и прокси назначены источнику. */
@Component({
  selector: 'app-source-connection-form',
  imports: [EntityPicker, FieldError, FormError],
  template: `
    <form class="card stack" (submit)="save($event)">
      <h2>Подключение источника</h2>
      <p class="hint">Бот и прокси, которые использует источник. При заданном прокси прямое соединение запрещено.</p>

      <div class="field">
        <span>Источник</span>
        <app-entity-picker [loader]="loadSources" [(value)]="sourceIds" placeholder="Выберите источник" />
      </div>

      @if (sourceIds().length) {
        <div class="row">
          <label class="field grow">
            <span>Telegram-бот</span>
            <select [value]="sessionId()" (change)="sessionId.set($any($event.target).value)">
              <option value="" [selected]="sessionId() === ''">Не задано</option>
              @for (item of sessions(); track item.id) {
                <option [value]="item.id" [selected]="sessionId() === item.id">{{ item.name }}</option>
              }
            </select>
            <app-field-error [error]="error()" field="telegramSessionId" />
          </label>
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
  protected readonly sessionId = signal('');
  protected readonly proxyId = signal('');
  protected readonly loading = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal<ApiError | null>(null);

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
        hint: SOURCE_TYPE_LABEL[source.sourceType],
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
      telegramSessionId: this.sessionId() || null,
      proxyId: this.proxyId() || null,
    };
  }

  private async load(id: string | undefined): Promise<void> {
    const token = ++this.token;
    this.error.set(null);
    if (!id) {
      this.sessionId.set('');
      this.proxyId.set('');
      this.loading.set(false);
      return;
    }
    this.loading.set(true);
    this.sessionId.set('');
    this.proxyId.set('');
    try {
      const connection = await this.sources.getConnection(id);
      if (token !== this.token) return;
      this.sessionId.set(connection.telegramSessionId ?? '');
      this.proxyId.set(connection.proxyId ?? '');
    } catch (err) {
      if (token !== this.token) return;
      this.error.set(toApiError(err));
      this.toasts.fromError(err);
    } finally {
      if (token === this.token) this.loading.set(false);
    }
  }
}
