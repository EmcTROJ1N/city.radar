import { Component, input } from '@angular/core';

@Component({
  selector: 'app-page-head',
  template: `
    <header class="page-head">
      <div>
        <h1>{{ title() }}</h1>
        @if (lead()) { <p class="lead">{{ lead() }}</p> }
      </div>
      <div class="row"><ng-content /></div>
    </header>`,
})
export class PageHead {
  readonly title = input.required<string>();
  readonly lead = input('');
}
