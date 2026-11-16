import { Component, computed, inject, signal } from '@angular/core';
import { AdminApi } from '@entities/user';
import type { SecretKind, SecretMeta } from '@shared/api/types';
import { agoIso, dateTime } from '@shared/lib/format';
import { Icon } from '@shared/ui/icon';
import { ToastService } from '@shared/ui/toast';
import { LlmSecretForm } from './llm-secret-form';
import { ProxySecretForm } from './proxy-secret-form';
import { SourceConnectionForm } from './source-connection-form';
import { TelegramSecretForm } from './telegram-secret-form';

const KIND_LABEL: Record<SecretKind, string> = {
  telegram_session: 'Telegram-бот',
  proxy: 'Прокси',
  llm: 'LLM',
};

/** Секреты и подключение источников. */
@Component({
  selector: 'app-admin-secrets',
  imports: [Icon, LlmSecretForm, ProxySecretForm, SourceConnectionForm, TelegramSecretForm],
  template: `
    <p class="callout callout--info">
      <app-icon name="lock" />
      <span>Значения секретов после сохранения никому не показываются, включая администраторов. Чтобы сменить секрет, введите новое значение. Модераторы этот раздел не видят.</span>
    </p>
    <p class="callout"><app-icon name="alert" /><span>При заданном прокси прямое соединение запрещено.</span></p>

    <div class="card card--flush table-wrap">
      <table class="table">
        <thead>
          <tr><th>Вид</th><th>Название</th><th>Прокси</th><th>Обновлён</th></tr>
        </thead>
        <tbody>
          @if (loading() && !items().length) {
            <tr><td colspan="4" class="empty">Загрузка…</td></tr>
          } @else {
            @for (item of items(); track item.id) {
              <tr>
                <td>{{ kind(item.kind) }}</td>
                <td><strong>{{ item.name }}</strong></td>
                <td>{{ proxyName(item.proxyId) }}</td>
                <td>
                  {{ updated(item.updatedAt) }}
                  <small class="muted">{{ ago(item.updatedAt) }}</small>
                </td>
              </tr>
            } @empty {
              <tr><td colspan="4" class="empty">Секреты ещё не заданы.</td></tr>
            }
          }
        </tbody>
      </table>
    </div>

    <div class="forms">
      <app-telegram-secret-form
        [sessions]="telegramSessions()"
        [proxies]="proxies()"
        (saved)="reload()"
      />
      <app-proxy-secret-form (saved)="reload()" />
      <app-llm-secret-form [proxies]="proxies()" (saved)="reload()" />
    </div>

    <app-source-connection-form [sessions]="telegramSessions()" [proxies]="proxies()" />`,
  styles: `
    :host { display: grid; gap: var(--gap); }
    .forms { display: grid; gap: var(--gap); grid-template-columns: repeat(auto-fit, minmax(320px, 1fr)); align-items: start; }
    .muted { color: var(--ink-3); }
  `,
})
export class AdminSecrets {
  private readonly api = inject(AdminApi);
  private readonly toasts = inject(ToastService);

  protected readonly items = signal<SecretMeta[]>([]);
  protected readonly loading = signal(false);
  protected readonly proxies = computed(() => this.items().filter((item) => item.kind === 'proxy'));
  protected readonly telegramSessions = computed(() =>
    this.items().filter((item) => item.kind === 'telegram_session'),
  );

  private token = 0;

  constructor() {
    void this.load();
  }

  protected kind(value: SecretKind): string {
    return KIND_LABEL[value];
  }

  protected proxyName(id: string | null | undefined): string {
    if (!id) return '—';
    return this.proxies().find((item) => item.id === id)?.name ?? id;
  }

  protected updated(iso: string): string {
    return dateTime(iso);
  }

  protected ago(iso: string): string {
    return agoIso(iso);
  }

  protected reload(): void {
    void this.load();
  }

  private async load(): Promise<void> {
    const token = ++this.token;
    this.loading.set(true);
    try {
      const items = await this.api.listSecrets();
      if (token !== this.token) return;
      this.items.set(items);
    } catch (err) {
      if (token !== this.token) return;
      this.toasts.fromError(err);
    } finally {
      if (token === this.token) this.loading.set(false);
    }
  }
}
