import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { OfflineIndicator } from './offline-indicator';

function setOnline(value: boolean): void {
  Object.defineProperty(navigator, 'onLine', { configurable: true, get: () => value });
}

describe('OfflineIndicator', () => {
  afterEach(() => {
    delete (navigator as unknown as Record<string, unknown>)['onLine'];
  });

  it('показывает «Нет сети» после события offline и скрывает после online', () => {
    const fixture = TestBed.createComponent(OfflineIndicator);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).not.toContain('Нет сети');

    setOnline(false);
    window.dispatchEvent(new Event('offline'));
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Нет сети');

    setOnline(true);
    window.dispatchEvent(new Event('online'));
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).not.toContain('Нет сети');

    fixture.destroy();
  });
});
