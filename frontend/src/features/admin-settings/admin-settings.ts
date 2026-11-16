import { Component, computed, inject, signal } from '@angular/core';
import { AdminApi, SessionService } from '@entities/user';
import { ApiError, toApiError } from '@shared/api/api-error';
import type { GlobalSettings, SecretMeta } from '@shared/api/types';
import { P } from '@shared/lib/permissions';
import { FieldError } from '@shared/ui/field-error';
import { FormError } from '@shared/ui/form-error';
import { ToastService } from '@shared/ui/toast';
import { SETTINGS_DEFAULTS, formFromSettings, settingsPatch, SettingsForm } from './settings-patch';

type NumericField = Exclude<keyof SettingsForm, 'proxySecretId'>;

/** Глобальные настройки: отправляются только изменённые поля. */
@Component({
  selector: 'app-admin-settings',
  imports: [FieldError, FormError],
  template: `
    @if (loading()) {
      <div class="card empty">Загрузка настроек…</div>
    } @else if (!settings()) {
      <div class="card empty">Не удалось загрузить настройки.</div>
    } @else {
      <form class="card stack" (submit)="save($event)">
        <h2>Глобальные настройки</h2>

        <div class="row">
          <label class="field grow">
            <span>Лимит отметок на пользователя в час</span>
            <input
              type="number"
              min="1"
              step="1"
              required
              [value]="number(form().observationRatePerHour)"
              (input)="setNumber('observationRatePerHour', $event)"
            />
            <span class="hint">Сколько статусов один пользователь может отметить за час.</span>
            <app-field-error [error]="error()" field="observationRatePerHour" />
          </label>
          <label class="field grow">
            <span>Максимальная длина комментария к отметке</span>
            <input
              type="number"
              min="1"
              max="5000"
              step="1"
              required
              [value]="number(form().commentMaxLength)"
              (input)="setNumber('commentMaxLength', $event)"
            />
            <span class="hint">От 1 до 5000 символов.</span>
            <app-field-error [error]="error()" field="commentMaxLength" />
          </label>
        </div>

        <div class="row">
          <label class="field grow">
            <span>Порог уверенности LLM по умолчанию</span>
            <input
              type="number"
              min="0"
              max="1"
              step="0.05"
              required
              [value]="number(form().confidenceThresholdDefault)"
              (input)="setNumber('confidenceThresholdDefault', $event)"
            />
            <span class="hint">От 0 до 1; ниже порога сообщение уходит на модерацию.</span>
            <app-field-error [error]="error()" field="confidenceThresholdDefault" />
          </label>
          <label class="field grow">
            <span>Доля таймаута, до которой статус считается свежим</span>
            <input
              type="number"
              min="0.01"
              max="0.99"
              step="0.01"
              required
              [value]="number(form().freshnessFreshRatio)"
              (input)="setNumber('freshnessFreshRatio', $event)"
            />
            <span class="hint">Больше 0 и меньше 1.</span>
            <app-field-error [error]="error()" field="freshnessFreshRatio" />
          </label>
        </div>

        <div class="row">
          <label class="field grow">
            <span>Окно детектора аномалий, мин</span>
            <input
              type="number"
              min="1"
              step="1"
              required
              [value]="number(form().anomalyWindowMinutes)"
              (input)="setNumber('anomalyWindowMinutes', $event)"
            />
            <app-field-error [error]="error()" field="anomalyWindowMinutes" />
          </label>
          <label class="field grow">
            <span>Порог отметок в окне, после которого фиксация замораживается</span>
            <input
              type="number"
              min="1"
              step="1"
              required
              [value]="number(form().anomalyThreshold)"
              (input)="setNumber('anomalyThreshold', $event)"
            />
            <app-field-error [error]="error()" field="anomalyThreshold" />
          </label>
        </div>

        <label class="field">
          <span>Глобальный прокси</span>
          @if (canReadSecrets()) {
            <select [value]="form().proxySecretId" (change)="setProxy($any($event.target).value)">
              <option value="" [selected]="form().proxySecretId === ''">Не задан</option>
              @for (proxy of proxies(); track proxy.id) {
                <option [value]="proxy.id" [selected]="form().proxySecretId === proxy.id">{{ proxy.name }}</option>
              }
            </select>
            <span class="hint">Секрет вида proxy; используется, когда прокси не задан у источника или LLM.</span>
          } @else {
            <span class="hint">Список прокси доступен с правом source.secrets.manage.</span>
          }
          <app-field-error [error]="error()" field="proxySecretId" />
        </label>

        <app-form-error
          [error]="error()"
          [fields]="[
            'observationRatePerHour',
            'commentMaxLength',
            'confidenceThresholdDefault',
            'anomalyWindowMinutes',
            'anomalyThreshold',
            'freshnessFreshRatio',
            'proxySecretId'
          ]"
        />

        <div class="row">
          <button class="btn btn--primary" type="submit" [disabled]="!valid() || !changedCount() || busy()">
            {{ busy() ? 'Сохранение…' : 'Сохранить настройки' }}
          </button>
          @if (changedCount()) { <span class="hint">Изменено полей: {{ changedCount() }}</span> }
        </div>
      </form>
    }`,
})
export class AdminSettings {
  private readonly api = inject(AdminApi);
  private readonly session = inject(SessionService);
  private readonly toasts = inject(ToastService);

  protected readonly settings = signal<GlobalSettings | null>(null);
  protected readonly form = signal<SettingsForm>({ ...SETTINGS_DEFAULTS });
  protected readonly secrets = signal<SecretMeta[]>([]);
  protected readonly loading = signal(true);
  protected readonly busy = signal(false);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly canReadSecrets = computed(() => this.session.has(P.secretsManage));
  protected readonly proxies = computed(() => this.secrets().filter((item) => item.kind === 'proxy'));

  protected readonly changedCount = computed(() => {
    const current = this.settings();
    return current ? Object.keys(settingsPatch(current, this.form())).length : 0;
  });

  protected readonly valid = computed(() => {
    const form = this.form();
    return (
      Number.isInteger(form.observationRatePerHour) &&
      form.observationRatePerHour >= 1 &&
      Number.isInteger(form.commentMaxLength) &&
      form.commentMaxLength >= 1 &&
      form.commentMaxLength <= 5000 &&
      Number.isFinite(form.confidenceThresholdDefault) &&
      form.confidenceThresholdDefault >= 0 &&
      form.confidenceThresholdDefault <= 1 &&
      Number.isInteger(form.anomalyWindowMinutes) &&
      form.anomalyWindowMinutes >= 1 &&
      Number.isInteger(form.anomalyThreshold) &&
      form.anomalyThreshold >= 1 &&
      Number.isFinite(form.freshnessFreshRatio) &&
      form.freshnessFreshRatio > 0 &&
      form.freshnessFreshRatio < 1
    );
  });

  constructor() {
    void this.load();
  }

  protected number(value: number): number | string {
    return Number.isFinite(value) ? value : '';
  }

  protected setNumber(field: NumericField, event: Event): void {
    const raw = (event.target as HTMLInputElement).value;
    this.form.update((value) => ({ ...value, [field]: raw === '' ? Number.NaN : Number(raw) }));
  }

  protected setProxy(value: string): void {
    this.form.update((form) => ({ ...form, proxySecretId: value }));
  }

  protected async save(event: Event): Promise<void> {
    event.preventDefault();
    const current = this.settings();
    if (!current || !this.valid() || this.busy()) return;
    const patch = settingsPatch(current, this.form());
    if (!Object.keys(patch).length) {
      this.toasts.info('Изменений нет');
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    try {
      const updated = await this.api.updateSettings(patch);
      this.settings.set(updated);
      this.form.set(formFromSettings(updated));
      this.toasts.success('Настройки сохранены');
    } catch (err) {
      this.error.set(toApiError(err));
      this.toasts.fromError(err);
    } finally {
      this.busy.set(false);
    }
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const settings = await this.api.getSettings();
      this.settings.set(settings);
      this.form.set(formFromSettings(settings));
      if (this.canReadSecrets()) {
        try {
          this.secrets.set(await this.api.listSecrets());
        } catch {
          this.secrets.set([]);
        }
      }
    } catch (err) {
      this.error.set(toApiError(err));
      this.toasts.fromError(err);
    } finally {
      this.loading.set(false);
    }
  }
}
