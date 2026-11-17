import { Component, effect, inject, input, output, signal } from '@angular/core';
import { SourcesApi, sourceStateIcon, sourceStateTone } from '@entities/source';
import { ApiError, errorText, toApiError } from '@shared/api/api-error';
import type { SourceHealth } from '@shared/api/types';
import { dateTime } from '@shared/lib/format';
import { SOURCE_STATE_LABEL } from '@shared/lib/labels';
import { Icon } from '@shared/ui/icon';
import { StatusBadge } from '@shared/ui/status-badge';
import { ToastService } from '@shared/ui/toast';

/** Состояние выбранного источника: последнее сообщение, FloodWait и ошибка. */
@Component({
  selector: 'app-source-health',
  imports: [Icon, StatusBadge],
  template: `
    <section class="card stack" aria-labelledby="source-health-title" [attr.aria-busy]="loading()">
      <div class="row">
        <h2 id="source-health-title">Состояние</h2>
        <span class="grow"></span>
        <button class="btn btn--ghost btn--sm" type="button" [disabled]="loading()" (click)="load(true)">
          <app-icon name="refresh" [class.is-spinning]="loading()" />
          {{ loading() ? 'Обновление…' : 'Обновить' }}
        </button>
      </div>

      @if (error(); as problem) {
        <div class="callout callout--alert" role="alert">
          <app-icon name="alert" />
          <span class="grow">{{ message(problem) }}</span>
          <button class="btn btn--sm" type="button" [disabled]="loading()" (click)="load(true)">
            Повторить
          </button>
        </div>
      } @else if (health(); as value) {
        <dl class="kv">
          <dt>Состояние</dt>
          <dd>
            <app-status-badge [label]="labels[value.state]" [tone]="tone(value.state)" [icon]="icon(value.state)" />
          </dd>
          <dt>Последнее сообщение</dt>
          <dd>{{ value.lastMessageAt ? dateTime(value.lastMessageAt) : 'Нет данных' }}</dd>
          <dt>FloodWait до</dt>
          <dd>{{ value.floodWaitUntil ? dateTime(value.floodWaitUntil) : 'Нет' }}</dd>
          <dt>Последняя ошибка</dt>
          <dd>{{ value.lastError || 'Нет' }}</dd>
        </dl>
      } @else {
        <p class="muted">Загрузка…</p>
      }
    </section>`,
  styles: `
    :host { display: block; }
    // Индикатор запроса: иконка крутится, пока идёт обращение к API.
    app-icon.is-spinning { animation: health-spin 0.9s linear infinite; }
    @keyframes health-spin { to { transform: rotate(360deg); } }
    // Ошибка появляется заново на каждую неудачную попытку, в том числе с тем же текстом.
    .callout--alert { animation: health-alert-in 0.18s ease-out; }
    @keyframes health-alert-in {
      from { opacity: 0; transform: translateY(-4px); }
      to { opacity: 1; transform: none; }
    }
    @media (prefers-reduced-motion: reduce) {
      app-icon.is-spinning { animation: none; }
      .callout--alert { animation: none; }
    }
  `,
})
export class SourceHealthPanel {
  private readonly api = inject(SourcesApi);
  private readonly toasts = inject(ToastService);

  readonly sourceId = input.required<string>();
  readonly refreshed = output<SourceHealth>();

  protected readonly health = signal<SourceHealth | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly labels = SOURCE_STATE_LABEL;
  protected readonly tone = sourceStateTone;
  protected readonly icon = sourceStateIcon;
  protected readonly message = errorText;
  protected readonly dateTime = dateTime;

  private token = 0;

  constructor() {
    effect(() => {
      this.sourceId();
      void this.load();
    });
  }

  /** Перечитывает состояние источника; ручной вызов дополнительно подтверждает ошибку тостом. */
  protected async load(manual = false): Promise<void> {
    const id = this.sourceId();
    const token = ++this.token;
    this.loading.set(true);
    this.error.set(null);
    try {
      const value = await this.api.health(id);
      if (token !== this.token) return;
      this.health.set(value);
      this.refreshed.emit(value);
    } catch (err) {
      if (token !== this.token) return;
      const problem = toApiError(err);
      this.error.set(problem);
      // Новый экземпляр ошибки заставляет шаблон пересоздать плашку: повторное
      // нажатие «Обновить» снова показывает тот же текст, а не оставляет «ничего не произошло».
      if (manual) this.toasts.error(this.message(problem));
    } finally {
      if (token === this.token) this.loading.set(false);
    }
  }
}
