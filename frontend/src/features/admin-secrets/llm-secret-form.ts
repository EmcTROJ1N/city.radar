import { Component, computed, inject, input, output, signal } from '@angular/core';
import { AdminApi } from '@entities/user';
import { ApiError, toApiError } from '@shared/api/api-error';
import type { SecretMeta } from '@shared/api/types';
import { FieldError } from '@shared/ui/field-error';
import { FormError } from '@shared/ui/form-error';
import { ToastService } from '@shared/ui/toast';

/** Ключ, адрес и модель LLM; текущие значения не отдаются, форма после сохранения очищается. */
@Component({
  selector: 'app-llm-secret-form',
  imports: [FieldError, FormError],
  template: `
    <form class="card stack" (submit)="save($event)">
      <h2>LLM</h2>
      <p class="hint">Текущие значения не отображаются; чтобы изменить, введите заново.</p>

      <label class="field">
        <span>Ключ API</span>
        <input
          id="llm-key"
          type="password"
          autocomplete="off"
          [value]="apiKey()"
          (input)="apiKey.set($any($event.target).value)"
        />
        <app-field-error [error]="error()" field="apiKey" />
      </label>

      <label class="field">
        <span>Base URL</span>
        <input
          id="llm-url"
          type="url"
          autocomplete="off"
          placeholder="https://…"
          [value]="baseUrl()"
          (input)="baseUrl.set($any($event.target).value)"
        />
        <app-field-error [error]="error()" field="baseUrl" />
      </label>

      <div class="row">
        <label class="field grow">
          <span>Модель</span>
          <input id="llm-model" [value]="model()" (input)="model.set($any($event.target).value)" autocomplete="off" />
          <span class="hint">Перед сохранением модель проверяется запросом к провайдеру.</span>
          <app-field-error [error]="error()" field="model" />
        </label>
        <label class="field grow">
          <span>Прокси</span>
          <select [value]="proxyId()" (change)="proxyId.set($any($event.target).value)">
            <option value="" [selected]="proxyId() === ''">Без прокси</option>
            @for (proxy of proxies(); track proxy.id) {
              <option [value]="proxy.id" [selected]="proxyId() === proxy.id">{{ proxy.name }}</option>
            }
          </select>
          <app-field-error [error]="error()" field="proxyId" />
        </label>
      </div>

      <app-form-error [error]="error()" [fields]="['apiKey', 'baseUrl', 'model', 'proxyId']" />

      <div class="row">
        <button class="btn btn--primary" type="submit" [disabled]="!valid() || busy()">
          {{ busy() ? 'Сохранение…' : 'Сохранить LLM' }}
        </button>
      </div>
    </form>`,
})
export class LlmSecretForm {
  private readonly api = inject(AdminApi);
  private readonly toasts = inject(ToastService);

  readonly proxies = input<readonly SecretMeta[]>([]);
  readonly saved = output<void>();

  protected readonly apiKey = signal('');
  protected readonly baseUrl = signal('');
  protected readonly model = signal('');
  protected readonly proxyId = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal<ApiError | null>(null);

  protected readonly valid = computed(() => {
    if (!this.apiKey().trim() || !this.model().trim() || !this.baseUrl().trim()) return false;
    try {
      new URL(this.baseUrl().trim());
      return true;
    } catch {
      return false;
    }
  });

  protected async save(event: Event): Promise<void> {
    event.preventDefault();
    if (!this.valid() || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.api.putLlm({
        apiKey: this.apiKey().trim(),
        baseUrl: this.baseUrl().trim(),
        model: this.model().trim(),
        proxyId: this.proxyId() || null,
      });
      this.toasts.success('Настройки LLM сохранены');
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
    this.apiKey.set('');
    this.baseUrl.set('');
    this.model.set('');
    this.proxyId.set('');
  }
}
