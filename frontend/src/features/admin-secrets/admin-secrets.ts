import { Component, computed, inject, signal } from '@angular/core';
import { AdminApi } from '@entities/user';
import type { SecretKind, SecretMeta } from '@shared/api/types';
import { agoIso, dateTime } from '@shared/lib/format';
import { Icon } from '@shared/ui/icon';
import { TabItem, Tabs } from '@shared/ui/tabs';
import { ToastService } from '@shared/ui/toast';
import { LlmSecretForm } from './llm-secret-form';
import { ProxySecretForm } from './proxy-secret-form';
import { SourceConnectionForm } from './source-connection-form';
import { TelegramSecretForm } from './telegram-secret-form';

const KIND_LABEL: Record<SecretKind, string> = {
  telegram_session: 'Telegram',
  proxy: 'Прокси',
  llm: 'LLM',
};

const FORM_TABS: TabItem[] = [
  { id: 'telegram', label: 'Telegram', icon: 'send' },
  { id: 'proxy', label: 'Прокси', icon: 'shield' },
  { id: 'llm', label: 'LLM', icon: 'settings' },
];

/** Секреты и подключение источников. */
@Component({
  selector: 'app-admin-secrets',
  imports: [Icon, LlmSecretForm, ProxySecretForm, SourceConnectionForm, Tabs, TelegramSecretForm],
  template: `
    <p class="callout callout--info">
      <app-icon name="lock" />
      <span>Значения секретов после сохранения никому не показываются, включая администраторов. Чтобы сменить секрет, введите новое значение. Модераторы этот раздел не видят.</span>
    </p>
    <p class="callout"><app-icon name="alert" /><span>При заданном прокси прямое соединение запрещено.</span></p>

    <app-source-connection-form [sessions]="telegramSessions()" [proxies]="proxies()" />

    <section class="card card--flush secrets" aria-labelledby="secrets-title">
      <header class="secrets__head">
        <h2 id="secrets-title">Сохранённые секреты</h2>
        <span class="muted">{{ items().length }} {{ items().length === 1 ? 'запись' : 'записей' }}</span>
      </header>

      @if (loading() && !items().length) {
        <div class="empty">Загрузка…</div>
      } @else {
        @for (item of items(); track item.id) {
          <article class="secret">
            <div class="secret__main">
              <span class="secret__kind">{{ kindLabel(item) }}</span>
              <strong class="secret__name">{{ item.name }}</strong>
            </div>
            <div class="secret__meta">
              <span><span class="muted">прокси:</span> {{ proxyName(item.proxyId) }}</span>
              <span class="secret__when">
                {{ updated(item.updatedAt) }}
                <small>{{ ago(item.updatedAt) }}</small>
              </span>
            </div>
          </article>
        } @empty {
          <div class="empty">Секреты ещё не заданы.</div>
        }
      }
    </section>

    <div class="creation">
      <app-tabs [tabs]="formTabs" [active]="activeForm()" (activeChange)="activeForm.set($event)" />

      @switch (activeForm()) {
        @case ('telegram') {
          <app-telegram-secret-form
            [sessions]="telegramSessions()"
            [proxies]="proxies()"
            (saved)="reload()"
          />
        }
        @case ('proxy') {
          <app-proxy-secret-form (saved)="reload()" />
        }
        @case ('llm') {
          <app-llm-secret-form [proxies]="proxies()" (saved)="reload()" />
        }
      }
    </div>`,
  styles: `
    :host { display: grid; gap: var(--gap); }
    .creation { display: grid; gap: 10px; }
    .secrets__head { display: flex; flex-wrap: wrap; gap: 6px 12px; align-items: baseline;
      justify-content: space-between; padding: 13px 16px; border-bottom: 1px solid var(--line); }
    .secrets__head h2 { font-size: 16px; }
    .secrets__head .muted { font-size: 13px; }
    .secret { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 6px 20px;
      align-items: center; padding: 11px 16px; border-bottom: 1px solid var(--line-soft); }
    .secret:last-child { border-bottom: 0; }
    .secret__main { display: flex; align-items: center; gap: 10px; min-width: 0; }
    .secret__kind { flex: none; display: inline-flex; align-items: center; height: 24px; padding: 0 9px;
      border-radius: 999px; background: var(--surface-3); color: var(--ink-2); font-size: 12px; font-weight: 600; }
    .secret__name { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .secret__meta { display: flex; flex-wrap: wrap; align-items: center; justify-content: flex-end;
      gap: 6px 18px; color: var(--ink-2); font-size: 13px; }
    .secret__when { display: inline-flex; align-items: baseline; gap: 6px; }
    .secret__when small { color: var(--ink-3); }
    .muted { color: var(--ink-3); }
    @media (max-width: 900px) {
      .secret { grid-template-columns: 1fr; }
      .secret__meta { justify-content: flex-start; }
    }
  `,
})
export class AdminSecrets {
  private readonly api = inject(AdminApi);
  private readonly toasts = inject(ToastService);

  protected readonly items = signal<SecretMeta[]>([]);
  protected readonly loading = signal(false);
  protected readonly formTabs = FORM_TABS;
  protected readonly activeForm = signal('telegram');
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

  /** Вид секрета: для Telegram дополнительно показываем способ входа. */
  protected kindLabel(item: SecretMeta): string {
    if (item.kind === 'telegram_session') {
      return item.mode === 'userbot' ? 'Telegram MTProto' : 'Telegram-бот';
    }

    return this.kind(item.kind);
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
