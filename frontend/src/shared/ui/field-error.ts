import { Component, computed, input } from '@angular/core';
import { ApiError } from '../api/api-error';

@Component({
  selector: 'app-field-error',
  template: `@if (text()) { <p class="fe" role="alert">{{ text() }}</p> }`,
  styles: `
    .fe { font-size: 12px; color: var(--st-none); }
  `,
})
export class FieldError {
  readonly error = input<ApiError | null>(null);
  readonly field = input.required<string>();

  protected readonly text = computed(() => this.error()?.fieldErrors[this.field()] ?? '');
}
