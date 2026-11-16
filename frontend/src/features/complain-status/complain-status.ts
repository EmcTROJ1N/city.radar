import { Component, computed, inject, input, signal } from '@angular/core';
import { MapApi } from '@entities/map-object';
import { SessionService } from '@entities/user';
import { P } from '@shared/lib/permissions';
import { Icon } from '@shared/ui/icon';
import { ToastService } from '@shared/ui/toast';

const TEXT_MAX = 1000;

/** Жалоба на статус фиксации — попадает в очередь модерации. */
@Component({
  selector: 'app-complain-status',
  imports: [Icon],
  template: `
    @if (allowed()) {
      @if (!open()) {
        <button class="btn btn--ghost btn--sm" type="button" (click)="open.set(true)">
          <app-icon name="alert" />Пожаловаться на статус
        </button>
      } @else {
        <form class="stack" (submit)="submit($event)">
          <div class="field">
            <label for="cs-text">Что не так со статусом</label>
            <textarea id="cs-text" [value]="text()" [attr.maxlength]="max" (input)="text.set($any($event.target).value)"
              placeholder="Например: воды нет, хотя на карте указано, что есть"></textarea>
            <span class="hint">{{ text().length }}/{{ max }}</span>
          </div>
          <div class="row">
            <button class="btn btn--primary btn--sm" type="submit" [disabled]="!text().trim() || submitting()">Отправить</button>
            <button class="btn btn--ghost btn--sm" type="button" (click)="open.set(false)">Отмена</button>
          </div>
        </form>
      }
    }`,
})
export class ComplainStatus {
  private readonly api = inject(MapApi);
  private readonly session = inject(SessionService);
  private readonly toasts = inject(ToastService);

  readonly objectId = input.required<string>();
  readonly observationId = input<string | null>(null);

  protected readonly max = TEXT_MAX;
  protected readonly allowed = computed(() => this.session.has(P.observationCreate));
  protected readonly open = signal(false);
  protected readonly text = signal('');
  protected readonly submitting = signal(false);

  protected async submit(event: Event): Promise<void> {
    event.preventDefault();
    const text = this.text().trim();
    if (!text || this.submitting()) return;

    this.submitting.set(true);
    try {
      await this.api.complain(this.objectId(), {
        text,
        ...(this.observationId() ? { observationId: this.observationId()! } : {}),
      });
      this.toasts.success('Жалоба отправлена модераторам');
      this.text.set('');
      this.open.set(false);
    } catch (err) {
      this.toasts.fromError(err);
    } finally {
      this.submitting.set(false);
    }
  }
}
