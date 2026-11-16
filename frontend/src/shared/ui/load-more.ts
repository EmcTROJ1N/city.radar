import { Component, input, output } from '@angular/core';

@Component({
  selector: 'app-load-more',
  template: `
    @if (hasMore()) {
      <div class="lm">
        <button type="button" class="btn" [disabled]="loading()" (click)="more.emit()">
          {{ loading() ? 'Загрузка…' : 'Показать ещё' }}
        </button>
      </div>
    }`,
  styles: `
    .lm { display: flex; justify-content: center; padding: 8px; }
  `,
})
export class LoadMore {
  readonly hasMore = input(false);
  readonly loading = input(false);
  readonly more = output<void>();
}
