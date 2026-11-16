import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { EntityPicker } from './entity-picker';

const OPTIONS = [
  { value: 's1', label: 'Источник 1' },
  { value: 's2', label: 'Источник 2' },
];

@Component({
  imports: [EntityPicker],
  template: `<app-entity-picker [loader]="loader" [multiple]="true" [(value)]="value" />`,
})
class Host {
  readonly value = signal<string[]>([]);
  readonly loader = vi.fn(async (_q: string, _cursor: string | null) => ({
    items: OPTIONS,
    nextCursor: null as string | null,
  }));
}

async function render(): Promise<{ fixture: ComponentFixture<Host>; el: HTMLElement }> {
  const fixture = TestBed.createComponent(Host);
  fixture.detectChanges();
  return { fixture, el: fixture.nativeElement as HTMLElement };
}

async function settle(fixture: ComponentFixture<Host>): Promise<void> {
  await Promise.resolve();
  await Promise.resolve();
  fixture.detectChanges();
}

async function openPanel(fixture: ComponentFixture<Host>, el: HTMLElement): Promise<void> {
  (el.querySelector('.ep__btn') as HTMLButtonElement).click();
  fixture.detectChanges();
  await settle(fixture);
}

describe('EntityPicker', () => {
  it('после debounce грузит по введённому запросу', async () => {
    const { fixture, el } = await render();
    const host = fixture.componentInstance;
    await openPanel(fixture, el);
    expect(host.loader).toHaveBeenCalledWith('', null);

    const input = el.querySelector('input[type="search"]') as HTMLInputElement;
    input.value = 'вод';
    input.dispatchEvent(new Event('input'));
    expect(host.loader).toHaveBeenCalledTimes(1);

    await new Promise((resolve) => setTimeout(resolve, 350));
    await settle(fixture);

    expect(host.loader).toHaveBeenLastCalledWith('вод', null);
  });

  it('в multiple выбор добавляет значение, повторный клик убирает', async () => {
    const { fixture, el } = await render();
    const host = fixture.componentInstance;
    await openPanel(fixture, el);

    const options = el.querySelectorAll('[role="option"]');
    (options[0] as HTMLElement).click();
    fixture.detectChanges();
    expect(host.value()).toEqual(['s1']);

    (options[1] as HTMLElement).click();
    fixture.detectChanges();
    expect(host.value()).toEqual(['s1', 's2']);

    (options[0] as HTMLElement).click();
    fixture.detectChanges();
    expect(host.value()).toEqual(['s2']);
  });
});
