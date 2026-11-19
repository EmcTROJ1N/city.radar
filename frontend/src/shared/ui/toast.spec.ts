import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ToastService, Toasts } from './toast';

describe('Toasts', () => {
  let shown: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    // В jsdom нет Popover API: подставляем, чтобы увидеть вызовы.
    shown = vi.fn(function (this: HTMLElement) {
      this.setAttribute('data-shown', '');
    });
    Object.assign(HTMLElement.prototype, { showPopover: shown, hidePopover: vi.fn() });
  });

  afterEach(() => {
    delete (HTMLElement.prototype as Partial<HTMLElement>).showPopover;
    delete (HTMLElement.prototype as Partial<HTMLElement>).hidePopover;
    TestBed.resetTestingModule();
  });

  it('тосты показываются поверх модальных окон через popover', () => {
    const fixture = TestBed.createComponent(Toasts);
    const service = TestBed.inject(ToastService);
    fixture.detectChanges();

    service.success('Наблюдение отозвано');
    fixture.detectChanges();

    const box = (fixture.nativeElement as HTMLElement).querySelector('.toasts') as HTMLElement;
    expect(box.getAttribute('popover')).toBe('manual');
    expect(shown).toHaveBeenCalledTimes(1);
  });

  it('новый тост заново поднимает контейнер наверх', () => {
    const fixture = TestBed.createComponent(Toasts);
    const service = TestBed.inject(ToastService);
    fixture.detectChanges();

    service.success('Первый');
    fixture.detectChanges();
    service.error('Второй');
    fixture.detectChanges();

    expect(shown).toHaveBeenCalledTimes(2);
  });
});
