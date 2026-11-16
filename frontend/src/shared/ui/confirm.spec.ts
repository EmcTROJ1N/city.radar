import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { ConfirmHost, ConfirmService } from './confirm';

function button(el: HTMLElement, text: string): HTMLButtonElement {
  const found = [...el.querySelectorAll('button')].find((b) => b.textContent?.trim() === text);
  if (!found) throw new Error(`Кнопка «${text}» не найдена`);
  return found;
}

describe('ConfirmService', () => {
  it('подтверждение возвращает confirmed true и причину', async () => {
    const fixture = TestBed.createComponent(ConfirmHost);
    const service = TestBed.inject(ConfirmService);
    fixture.detectChanges();

    const result = service.ask({ title: 'Удалить статус?', reasonLabel: 'Причина', reasonRequired: true });
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('dialog')?.hasAttribute('open')).toBe(true);

    const area = el.querySelector('textarea') as HTMLTextAreaElement;
    area.value = 'Дубликат';
    area.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    const confirm = button(el, 'Подтвердить');
    expect(confirm.disabled).toBe(false);
    confirm.click();

    await expect(result).resolves.toEqual({ confirmed: true, reason: 'Дубликат' });
    fixture.detectChanges();
    expect(el.querySelector('dialog')?.hasAttribute('open')).toBe(false);
  });

  it('без причины подтвердить нельзя, отмена возвращает confirmed false', async () => {
    const fixture = TestBed.createComponent(ConfirmHost);
    const service = TestBed.inject(ConfirmService);
    fixture.detectChanges();

    const result = service.ask({ title: 'Отклонить?', reasonLabel: 'Причина', reasonRequired: true });
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    expect(button(el, 'Подтвердить').disabled).toBe(true);

    button(el, 'Отмена').click();

    await expect(result).resolves.toEqual({ confirmed: false, reason: '' });
    fixture.detectChanges();
    expect(el.querySelector('dialog')?.hasAttribute('open')).toBe(false);
  });
});
