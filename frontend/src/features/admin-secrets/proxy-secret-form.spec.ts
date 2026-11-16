import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AdminApi } from '@entities/user';
import { ToastService } from '@shared/ui/toast';
import { ProxySecretForm } from './proxy-secret-form';

function fill(element: HTMLInputElement, value: string): void {
  element.value = value;
  element.dispatchEvent(new Event('input'));
}

describe('ProxySecretForm', () => {
  let putProxy: ReturnType<typeof vi.fn>;

  function setup(): ComponentFixture<ProxySecretForm> {
    const fixture = TestBed.createComponent(ProxySecretForm);
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => {
    putProxy = vi.fn().mockResolvedValue(undefined);
    TestBed.configureTestingModule({
      providers: [
        { provide: AdminApi, useValue: { putProxy } },
        {
          provide: ToastService,
          useValue: { success: vi.fn(), error: vi.fn(), info: vi.fn(), fromError: vi.fn(), dismiss: vi.fn() },
        },
      ],
    });
  });

  it('очищает пароль после сохранения', async () => {
    const fixture = setup();
    const element = fixture.nativeElement as HTMLElement;

    fill(element.querySelector<HTMLInputElement>('#px-name')!, 'Московский');
    fill(element.querySelector<HTMLInputElement>('#px-host')!, 'proxy.local');
    fill(element.querySelector<HTMLInputElement>('#px-password')!, 'секрет');
    fixture.detectChanges();

    element.querySelector('form')!.dispatchEvent(new Event('submit'));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(putProxy).toHaveBeenCalledTimes(1);
    const [id, body] = putProxy.mock.calls[0] as [string, Record<string, unknown>];
    expect(typeof id).toBe('string');
    expect(body).toMatchObject({ name: 'Московский', host: 'proxy.local', port: 1080, password: 'секрет' });
    expect(element.querySelector<HTMLInputElement>('#px-password')!.value).toBe('');
  });

  it('не отправляет пустой пароль', async () => {
    const fixture = setup();
    const element = fixture.nativeElement as HTMLElement;

    fill(element.querySelector<HTMLInputElement>('#px-name')!, 'Резервный');
    fill(element.querySelector<HTMLInputElement>('#px-host')!, 'proxy2.local');

    element.querySelector('form')!.dispatchEvent(new Event('submit'));
    await fixture.whenStable();

    expect(putProxy).toHaveBeenCalledTimes(1);
    const body = putProxy.mock.calls[0]![1] as Record<string, unknown>;
    expect('password' in body).toBe(false);
  });
});
