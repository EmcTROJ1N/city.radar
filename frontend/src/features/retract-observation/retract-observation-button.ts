import { Component, inject, input, output, signal } from '@angular/core';
import { ObservationsApi } from '@entities/observation';
import type { RetractResult } from '@shared/api/types';
import { ConfirmService } from '@shared/ui/confirm';
import { Icon } from '@shared/ui/icon';
import { ToastService } from '@shared/ui/toast';

/** Отзыв одного наблюдения с обязательной причиной. */
@Component({
  selector: 'app-retract-observation-button',
  imports: [Icon],
  template: `
    @if (iconOnly()) {
      <button type="button" class="btn btn--ghost btn--icon btn--sm btn--danger" title="Отозвать наблюдение"
        aria-label="Отозвать наблюдение" [disabled]="busy()" (click)="retract()">
        <app-icon name="trash" />
      </button>
    } @else {
      <button type="button" class="btn btn--ghost btn--sm btn--danger" [disabled]="busy()" (click)="retract()">
        <app-icon name="trash" />Отозвать наблюдение
      </button>
    }`,
})
export class RetractObservationButton {
  private readonly api = inject(ObservationsApi);
  private readonly confirm = inject(ConfirmService);
  private readonly toasts = inject(ToastService);

  readonly observationId = input.required<string>();
  readonly iconOnly = input(false);
  readonly retracted = output<RetractResult>();

  protected readonly busy = signal(false);

  protected async retract(): Promise<void> {
    if (this.busy()) return;

    const answer = await this.confirm.ask({
      title: 'Отозвать наблюдение?',
      text: 'Наблюдение перестанет учитываться в статусе фиксации.',
      confirmLabel: 'Отозвать',
      danger: true,
      reasonLabel: 'Причина',
      reasonRequired: true,
    });
    if (!answer.confirmed || !answer.reason) return;

    this.busy.set(true);
    try {
      const result = await this.api.retract(this.observationId(), { reason: answer.reason });
      this.toasts.success('Наблюдение отозвано');
      this.retracted.emit(result);
    } catch (err) {
      this.toasts.fromError(err);
    } finally {
      this.busy.set(false);
    }
  }
}
