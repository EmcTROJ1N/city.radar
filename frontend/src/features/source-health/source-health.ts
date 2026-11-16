import { Component, effect, inject, input, output, signal } from '@angular/core';
import { SourcesApi, sourceStateIcon, sourceStateTone } from '@entities/source';
import { ApiError, errorText, toApiError } from '@shared/api/api-error';
import type { SourceHealth } from '@shared/api/types';
import { dateTime } from '@shared/lib/format';
import { SOURCE_STATE_LABEL } from '@shared/lib/labels';
import { Icon } from '@shared/ui/icon';
import { StatusBadge } from '@shared/ui/status-badge';

/** Состояние выбранного источника: последнее сообщение, FloodWait и ошибка. */
@Component({
  selector: 'app-source-health',
  imports: [Icon, StatusBadge],
  template: `
    <section class="card stack" aria-labelledby="source-health-title">
      <div class="row">
        <h2 id="source-health-title">Состояние</h2>
        <span class="grow"></span>
        <button class="btn btn--ghost btn--sm" type="button" [disabled]="loading()" (click)="load()">
          <app-icon name="clock" />Обновить
        </button>
      </div>

      @if (error(); as problem) {
        <div class="callout" role="alert">
          <app-icon name="alert" />
          <span class="grow">{{ message(problem) }}</span>
          <button class="btn btn--sm" type="button" (click)="load()">Повторить</button>
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
})
export class SourceHealthPanel {
  private readonly api = inject(SourcesApi);

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

  protected async load(): Promise<void> {
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
      if (token === this.token) this.error.set(toApiError(err));
    } finally {
      if (token === this.token) this.loading.set(false);
    }
  }
}
