import { Component, ElementRef, effect, model, viewChild } from '@angular/core';

/** Дисклеймер о достоверности данных. */
@Component({
  selector: 'app-disclaimer',
  template: `
    <dialog #dialog class="dlg" aria-labelledby="disclaimer-title" (close)="open.set(false)">
      <h2 id="disclaimer-title">О достоверности данных</h2>
      <div class="stack">
        <p>Данные поступают из чатов (разбор автоматический, с помощью LLM) и от жителей. Достоверность не гарантируется.</p>
        <p>Статус фиксации — это самое свежее актуальное наблюдение. Все наблюдения равнозначны, поэтому победить может отметка любого источника.</p>
        <p>У каждого источника свой таймаут актуальности: когда он истекает, наблюдение перестаёт влиять на статус. Устаревшие и неизвестные статусы бледнеют.</p>
      </div>
      <div class="row">
        <button class="btn btn--primary" type="button" (click)="open.set(false)">Понятно</button>
      </div>
    </dialog>`,
  styles: `
    .dlg { width: min(520px, calc(100vw - 2 * var(--gap))); padding: var(--pad); border: 1px solid var(--line);
      border-radius: var(--r-lg); background: var(--surface); color: var(--ink); box-shadow: var(--shadow-float); }
    .dlg::backdrop { background: rgb(15 42 51 / 0.45); }
    .dlg h2 { margin-bottom: 10px; }
    .dlg p { color: var(--ink-2); }
    .dlg .row { margin-top: var(--pad); justify-content: flex-end; }
  `,
})
export class Disclaimer {
  readonly open = model(false);

  private readonly dialog = viewChild<ElementRef<HTMLDialogElement>>('dialog');

  constructor() {
    effect(() => {
      const element = this.dialog()?.nativeElement;
      if (!element) return;
      if (this.open()) {
        if (!element.open && typeof element.showModal === 'function') element.showModal();
      } else if (element.open) {
        element.close();
      }
    });
  }
}
