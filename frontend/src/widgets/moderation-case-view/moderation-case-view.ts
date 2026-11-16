import { Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { ModerationApi } from '@entities/moderation-case';
import { ResolveModerationCase } from '@features/resolve-moderation-case/resolve-moderation-case';
import { RetractObservationButton } from '@features/retract-observation/retract-observation-button';
import { errorText, toApiError } from '@shared/api/api-error';
import type { ModerationCase, ModerationCaseKind } from '@shared/api/types';
import { dateTime } from '@shared/lib/format';
import { CASE_KIND_LABEL, CASE_STATE_LABEL } from '@shared/lib/labels';
import { Icon } from '@shared/ui/icon';
import { highlightEvidence, Segment } from './highlight';

/** Подробности кейса: исходное сообщение с подсветкой evidence_quote и решение. */
@Component({
  selector: 'app-moderation-case-view',
  imports: [Icon, ResolveModerationCase, RetractObservationButton],
  template: `
    <article class="card stack case">
      @if (loading() && !item()) {
        <p class="muted">Загрузка…</p>
      } @else if (notFound()) {
        <p class="callout"><app-icon name="alert" /><span>Кейс не найден.</span></p>
      } @else if (error(); as message) {
        <p class="callout"><app-icon name="alert" /><span>{{ message }}</span></p>
      } @else if (item(); as c) {
        <header class="stack">
          <div class="row">
            <h2 class="grow">{{ kindLabel[c.kind] }}</h2>
            <span class="chip" [class.chip--alert]="isAlert(c.kind)">{{ stateLabel[c.state] }}</span>
          </div>
          <dl class="kv">
            <dt>Источник</dt><dd>{{ c.sourceName || '—' }}</dd>
            <dt>Фиксация</dt><dd>{{ c.mapObjectName || '—' }}</dd>
            <dt>Создан</dt><dd>{{ dateTime(c.createdAt) }}</dd>
          </dl>
        </header>

        @if (c.rawMessage; as message) {
          <section class="stack">
            <h3>Исходное сообщение</h3>
            <div class="msg">@for (segment of segments(); track $index) {@if (segment.mark) {<mark>{{ segment.text }}</mark>} @else {<span>{{ segment.text }}</span>}}</div>
            <div class="row">
              @if (message.author) { <small>Автор: {{ message.author }}</small> }
              <small>{{ dateTime(message.postedAt) }}</small>
              @if (message.link) {
                <a class="btn btn--ghost btn--sm" [href]="message.link" target="_blank" rel="noopener noreferrer">
                  <app-icon name="external" />Открыть сообщение
                </a>
              }
            </div>
            @if (quote() && !quoteFound()) {
              <p class="callout">
                <app-icon name="alert" />
                <span>Цитата: «{{ quote() }}» — <small>цитата не найдена в сообщении</small></span>
              </p>
            }
          </section>
        } @else if (c.messageExcerpt) {
          <section>
            <h3>Сообщение</h3>
            <blockquote>{{ c.messageExcerpt }}</blockquote>
          </section>
        }

        @if (c.proposed; as proposed) {
          <section>
            <h3>Предложение системы</h3>
            <div class="row">
              @if (proposed.statusCode) {
                <span class="chip">статус: {{ proposed.statusCode }}</span>
              } @else {
                <span class="chip">статус не предложен</span>
              }
              @if (proposed.confidence != null) {
                <span class="chip">уверенность {{ percent(proposed.confidence) }}</span>
              }
            </div>
          </section>
        }

        @if (c.complaintText) {
          <section>
            <h3>Текст жалобы</h3>
            <blockquote>{{ c.complaintText }}</blockquote>
          </section>
        }

        @if (c.state === 'open') {
          <app-resolve-moderation-case [item]="c" (resolved)="onResolved($event)" />
          @if (c.kind === 'complaint' && c.observationId) {
            <app-retract-observation-button [observationId]="c.observationId" (retracted)="onRetracted()" />
          }
        } @else {
          <footer class="row closed">
            <span class="chip">{{ stateLabel[c.state] }}</span>
            @if (c.resolvedAt) { <small>решение {{ dateTime(c.resolvedAt) }}</small> }
          </footer>
        }
      }
    </article>`,
  styles: `
    :host { display: block; }
    .case { align-content: start; }
    h2 { font-size: 20px; }
    h3 { margin-bottom: 6px; }
    .msg { white-space: pre-wrap; padding: 12px; border-radius: var(--r-md); background: var(--surface-2);
      border: 1px solid var(--line-soft); line-height: 1.5; }
    mark { background: color-mix(in srgb, var(--st-limited) 38%, transparent); color: inherit;
      padding: 0 2px; border-radius: 3px; }
    blockquote { margin: 0; padding-left: 10px; border-left: 3px solid var(--line); color: var(--ink-2); }
    .closed { padding-top: 4px; border-top: 1px solid var(--line-soft); }
    .chip--alert { background: color-mix(in srgb, var(--st-none) 14%, var(--surface)); color: var(--st-none); }
  `,
})
export class ModerationCaseView {
  private readonly api = inject(ModerationApi);

  readonly caseId = input.required<string>();
  readonly resolved = output<ModerationCase>();

  protected readonly kindLabel = CASE_KIND_LABEL;
  protected readonly stateLabel = CASE_STATE_LABEL;
  protected readonly dateTime = dateTime;

  protected readonly item = signal<ModerationCase | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly notFound = signal(false);

  protected readonly segments = computed<Segment[]>(() => {
    const message = this.item()?.rawMessage;
    if (!message) return [];
    return highlightEvidence(message.text, this.item()?.proposed?.evidenceQuote);
  });
  protected readonly quote = computed(() => this.item()?.proposed?.evidenceQuote ?? '');
  protected readonly quoteFound = computed(() => this.segments().some((segment) => segment.mark));

  private seq = 0;

  constructor() {
    effect(() => {
      const id = this.caseId();
      untracked(() => void this.load(id));
    });
  }

  protected isAlert(kind: ModerationCaseKind): boolean {
    return kind === 'anomaly' || kind === 'complaint';
  }

  protected percent(value: number): string {
    return `${Math.round(value * 100)}%`;
  }

  protected onResolved(updated: ModerationCase): void {
    this.item.set(updated);
    this.resolved.emit(updated);
  }

  protected onRetracted(): void {
    void this.load(this.caseId());
  }

  private async load(id: string): Promise<void> {
    const seq = ++this.seq;
    if (this.item()?.id !== id) this.item.set(null);
    this.loading.set(true);
    this.error.set(null);
    this.notFound.set(false);
    try {
      const item = await this.api.get(id);
      if (seq !== this.seq) return;
      this.item.set(item);
    } catch (err) {
      if (seq !== this.seq) return;
      const error = toApiError(err);
      if (error.status === 404) this.notFound.set(true);
      else this.error.set(errorText(error));
    } finally {
      if (seq === this.seq) this.loading.set(false);
    }
  }
}
