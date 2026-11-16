import { Component, computed, input } from '@angular/core';
import { ApiError, errorText, unhandledFieldErrors } from '../api/api-error';
import { Icon } from './icon';

/** Общая ошибка формы: то, что не показано у конкретных полей через <app-field-error>. */
@Component({
  selector: 'app-form-error',
  imports: [Icon],
  template: `
    @if (text(); as message) {
      <div class="callout" role="alert">
        <app-icon name="alert" />
        <div class="grow">
          <p>{{ message }}</p>
          @if (extra().length) {
            <ul class="fe__list">
              @for (item of extra(); track item.field) {
                <li>{{ item.field }}: {{ item.message }}</li>
              }
            </ul>
          }
        </div>
      </div>
    }`,
  styles: `
    .fe__list { margin: 6px 0 0; padding-left: 18px; font-size: 13px; }
  `,
})
export class FormError {
  readonly error = input<ApiError | null>(null);
  readonly fields = input<string[]>([]);

  protected readonly extra = computed(() => {
    const e = this.error();
    if (!e || e.status !== 422) return [];
    return unhandledFieldErrors(e, this.fields()).map((field) => ({
      field,
      message: e.fieldErrors[field] ?? '',
    }));
  });

  protected readonly text = computed(() => {
    const e = this.error();
    if (!e) return '';
    if (e.status !== 422) return errorText(e);
    const hasFields = Object.keys(e.fieldErrors).length > 0;
    return this.extra().length || !hasFields ? errorText(e) : '';
  });
}
