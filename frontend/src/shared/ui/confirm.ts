import { Component, ElementRef, Injectable, computed, effect, inject, signal, viewChild } from '@angular/core';

export interface ConfirmOptions {
  title: string;
  text?: string;
  confirmLabel?: string;
  danger?: boolean;
  reasonLabel?: string;
  reasonRequired?: boolean;
}

export interface ConfirmResult {
  confirmed: boolean;
  reason: string;
}

interface ConfirmState extends ConfirmOptions {
  id: number;
}

let seq = 0;

@Injectable({ providedIn: 'root' })
export class ConfirmService {
  private readonly state = signal<ConfirmState | null>(null);
  readonly current = this.state.asReadonly();
  private resolve: ((result: ConfirmResult) => void) | null = null;

  /** Показывает диалог. Повторный вызов отменяет предыдущий вопрос. */
  ask(opts: ConfirmOptions): Promise<ConfirmResult> {
    const previous = this.resolve;
    this.resolve = null;
    previous?.({ confirmed: false, reason: '' });
    this.state.set({ ...opts, id: ++seq });
    return new Promise<ConfirmResult>((resolve) => {
      this.resolve = resolve;
    });
  }

  close(result: ConfirmResult): void {
    const resolve = this.resolve;
    this.resolve = null;
    this.state.set(null);
    resolve?.(result);
  }
}

@Component({
  selector: 'app-confirm-host',
  template: `
    <dialog #dialog class="cf" aria-labelledby="confirm-title" (cancel)="onCancel($event)" (close)="onNativeClose()">
      @if (service.current(); as c) {
        <form class="cf__box" (submit)="onSubmit($event)">
          <h2 id="confirm-title">{{ c.title }}</h2>
          @if (c.text) { <p class="cf__text">{{ c.text }}</p> }
          @if (c.reasonLabel) {
            <label class="field">
              <span>{{ c.reasonLabel }}</span>
              <textarea rows="3" [value]="reason()" (input)="reason.set($any($event.target).value)"></textarea>
            </label>
          }
          <div class="row cf__actions">
            <button #cancelBtn type="button" class="btn" (click)="cancel()">Отмена</button>
            <button
              type="submit"
              class="btn"
              [class.btn--danger]="c.danger"
              [class.btn--primary]="!c.danger"
              [disabled]="blocked()"
            >{{ c.confirmLabel ?? 'Подтвердить' }}</button>
          </div>
        </form>
      }
    </dialog>`,
  styles: `
    .cf { width: 100%; max-width: min(480px, calc(100vw - 32px)); padding: 0; border: 0; background: transparent; }
    .cf::backdrop { background: rgb(15 42 51 / 0.45); }
    .cf__box { display: grid; gap: 12px; padding: var(--pad); background: var(--surface);
      border: 1px solid var(--line); border-radius: var(--r-lg); box-shadow: var(--shadow-float); }
    .cf__text { color: var(--ink-2); }
    .field > span { font-size: 13px; font-weight: 500; color: var(--ink-2); }
    .cf__actions { justify-content: flex-end; }
  `,
})
export class ConfirmHost {
  protected readonly service = inject(ConfirmService);
  protected readonly reason = signal('');

  private readonly dialog = viewChild<ElementRef<HTMLDialogElement>>('dialog');
  private readonly cancelBtn = viewChild<ElementRef<HTMLButtonElement>>('cancelBtn');

  protected readonly blocked = computed(() => {
    const opts = this.service.current();
    return Boolean(opts?.reasonRequired) && !this.reason().trim();
  });

  constructor() {
    effect(() => {
      const opts = this.service.current();
      const dialog = this.dialog()?.nativeElement;
      if (!dialog) return;
      if (opts) {
        this.reason.set('');
        this.openDialog(dialog);
        setTimeout(() => this.cancelBtn()?.nativeElement.focus());
      } else {
        this.closeDialog(dialog);
      }
    });
  }

  protected cancel(): void {
    this.service.close({ confirmed: false, reason: '' });
  }

  protected onCancel(event: Event): void {
    event.preventDefault();
    this.cancel();
  }

  protected onSubmit(event: Event): void {
    event.preventDefault();
    if (this.blocked()) return;
    this.service.close({ confirmed: true, reason: this.reason().trim() });
  }

  /** Диалог закрылся без нашего участия (например, программно) — считаем отменой. */
  protected onNativeClose(): void {
    if (this.service.current()) this.cancel();
  }

  // В jsdom нет showModal/close — открываем атрибутом.
  private openDialog(dialog: HTMLDialogElement): void {
    if (dialog.open) return;
    if (typeof dialog.showModal === 'function') dialog.showModal();
    else dialog.setAttribute('open', '');
  }

  private closeDialog(dialog: HTMLDialogElement): void {
    if (!dialog.open) return;
    if (typeof dialog.close === 'function') dialog.close();
    else dialog.removeAttribute('open');
  }
}
