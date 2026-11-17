import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AdminApi } from '@entities/user';
import { ApiError } from '@shared/api/api-error';
import { ToastService } from '@shared/ui/toast';
import { TelegramSecretForm } from './telegram-secret-form';

const VALID_TOKEN = '123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw';

function fill(element: HTMLInputElement | HTMLTextAreaElement, value: string): void {
  element.value = value;
  element.dispatchEvent(new Event('input'));
}

describe('TelegramSecretForm', () => {
  let putTelegramSession: ReturnType<typeof vi.fn>;

  function setup(): ComponentFixture<TelegramSecretForm> {
    const fixture = TestBed.createComponent(TelegramSecretForm);
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => {
    putTelegramSession = vi.fn().mockResolvedValue(undefined);
    TestBed.configureTestingModule({
      providers: [
        { provide: AdminApi, useValue: { putTelegramSession } },
        {
          provide: ToastService,
          useValue: { success: vi.fn(), error: vi.fn(), info: vi.fn(), fromError: vi.fn(), dismiss: vi.fn() },
        },
      ],
    });
  });

  it('отправляет токен бота как session', async () => {
    const fixture = setup();
    const element = fixture.nativeElement as HTMLElement;

    fill(element.querySelector<HTMLInputElement>('#tg-name')!, 'Бот района');
    fill(element.querySelector<HTMLInputElement>('#tg-token')!, VALID_TOKEN);
    fixture.detectChanges();

    element.querySelector('form')!.dispatchEvent(new Event('submit'));
    await fixture.whenStable();

    expect(putTelegramSession).toHaveBeenCalledTimes(1);
    const [id, body] = putTelegramSession.mock.calls[0] as [string, Record<string, unknown>];
    expect(typeof id).toBe('string');
    expect(body).toMatchObject({ name: 'Бот района', session: VALID_TOKEN, proxyId: null });
  });

  it('сохраняет MTProto-сессию отдельным способом входа', async () => {
    const fixture = setup();
    const element = fixture.nativeElement as HTMLElement;

    (element.querySelectorAll<HTMLButtonElement>('.mode-option')[1]!).click();
    fixture.detectChanges();

    fill(element.querySelector<HTMLInputElement>('#tg-name')!, 'Аккаунт района');
    fill(element.querySelector<HTMLInputElement>('#tg-api-id')!, '1234567');
    fill(
      element.querySelector<HTMLInputElement>('#tg-api-hash')!,
      '0123456789abcdef0123456789abcdef',
    );
    fill(
      element.querySelector<HTMLTextAreaElement>('#tg-session-data')!,
      '1BVtsOK4Bu0-test-session-data-value',
    );
    fixture.detectChanges();

    element.querySelector('form')!.dispatchEvent(new Event('submit'));
    await fixture.whenStable();

    expect(putTelegramSession).toHaveBeenCalledTimes(1);
    const [, body] = putTelegramSession.mock.calls[0] as [string, Record<string, unknown>];
    expect(body['mode']).toBe('userbot');
    expect(JSON.parse(body['session'] as string)).toEqual({
      apiId: 1234567,
      apiHash: '0123456789abcdef0123456789abcdef',
      sessionData: '1BVtsOK4Bu0-test-session-data-value',
    });
  });

  it('не отправляет строку, которая не похожа на токен бота', async () => {
    const fixture = setup();
    const element = fixture.nativeElement as HTMLElement;

    fill(element.querySelector<HTMLInputElement>('#tg-name')!, 'Бот района');
    fill(element.querySelector<HTMLInputElement>('#tg-token')!, '{"apiId":123}');
    fixture.detectChanges();

    element.querySelector('form')!.dispatchEvent(new Event('submit'));
    await fixture.whenStable();

    expect(putTelegramSession).not.toHaveBeenCalled();
    expect(element.textContent).toContain('Токен должен выглядеть так');
  });

  it('показывает ошибку прокси у поля, а не глотает её', async () => {
    putTelegramSession.mockRejectedValueOnce(
      new ApiError(422, 'Ошибка валидации', null, {
        proxyId: 'Прокси 10.0.0.1:1080 недоступен',
      }),
    );

    const fixture = setup();
    const element = fixture.nativeElement as HTMLElement;

    fill(element.querySelector<HTMLInputElement>('#tg-name')!, 'Бот района');
    fill(element.querySelector<HTMLInputElement>('#tg-token')!, VALID_TOKEN);
    fixture.detectChanges();

    element.querySelector('form')!.dispatchEvent(new Event('submit'));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(element.textContent).toContain('Прокси 10.0.0.1:1080 недоступен');
  });
});
