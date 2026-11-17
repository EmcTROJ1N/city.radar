import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SourcesApi } from '@entities/source';
import { ApiError } from '@shared/api/api-error';
import type { SourceHealth } from '@shared/api/types';
import { ToastService } from '@shared/ui/toast';
import { SourceHealthPanel } from './source-health';

const HEALTH: SourceHealth = {
  state: 'ok',
  lastMessageAt: '2026-11-16T09:12:00Z',
  floodWaitUntil: null,
  lastError: null,
};

/** Отложенный промис: позволяет проверить состояние «запрос в полёте». */
function deferred<T>(): {
  promise: Promise<T>;
  resolve: (value: T) => void;
  reject: (reason: unknown) => void;
} {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

const settle = async (): Promise<void> => {
  await new Promise((resolve) => setTimeout(resolve));
};

describe('SourceHealthPanel', () => {
  let health: ReturnType<typeof vi.fn>;
  let toasts: { error: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    health = vi.fn().mockResolvedValue(HEALTH);
    toasts = { error: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        { provide: SourcesApi, useValue: { health } },
        {
          provide: ToastService,
          useValue: { success: vi.fn(), error: toasts.error, info: vi.fn(), fromError: vi.fn(), dismiss: vi.fn() },
        },
      ],
    });
  });

  async function create(): Promise<ComponentFixture<SourceHealthPanel>> {
    const fixture = TestBed.createComponent(SourceHealthPanel);
    fixture.componentRef.setInput('sourceId', 'src-1');
    fixture.detectChanges();
    await settle();
    fixture.detectChanges();
    return fixture;
  }

  it('показывает индикатор загрузки, пока идёт запрос', async () => {
    const pending = deferred<SourceHealth>();
    health.mockReturnValueOnce(pending.promise);

    // Эффект с input запускает первую загрузку — создаём компонент вручную,
    // чтобы успеть проверить промежуточное состояние.
    const fixture = TestBed.createComponent(SourceHealthPanel);
    fixture.componentRef.setInput('sourceId', 'src-1');
    fixture.detectChanges();
    await settle();
    fixture.detectChanges();

    const section = fixture.nativeElement.querySelector('section') as HTMLElement;
    const button = fixture.nativeElement.querySelector('.btn') as HTMLButtonElement;
    expect(section.getAttribute('aria-busy')).toBe('true');
    expect(button.disabled).toBe(true);
    expect(button.textContent).toContain('Обновление…');
    expect(fixture.nativeElement.querySelector('app-icon.is-spinning')).not.toBeNull();

    pending.resolve(HEALTH);
    await settle();
    fixture.detectChanges();

    expect(section.getAttribute('aria-busy')).toBe('false');
    expect(button.disabled).toBe(false);
    expect(button.textContent).toContain('Обновить');
    expect(fixture.nativeElement.querySelector('app-icon.is-spinning')).toBeNull();
  });

  it('повторно выводит ту же ошибку после нажатия «Обновить» и подтверждает её тостом', async () => {
    const failure = new ApiError(500, 'Ошибка сервера', 'Адаптер недоступен');
    health.mockRejectedValue(failure);

    const fixture = await create();
    const firstCallout = fixture.nativeElement.querySelector('.callout') as HTMLElement;
    expect(firstCallout.textContent).toContain('Адаптер недоступен');
    expect(toasts.error).not.toHaveBeenCalled();

    const button = fixture.nativeElement.querySelector('.btn') as HTMLButtonElement;
    button.click();
    fixture.detectChanges();
    // На время запроса ошибка убрана, чтобы её появление было заметным.
    expect(fixture.nativeElement.querySelector('.callout')).toBeNull();

    await settle();
    fixture.detectChanges();

    const secondCallout = fixture.nativeElement.querySelector('.callout') as HTMLElement;
    expect(secondCallout).not.toBeNull();
    expect(secondCallout).not.toBe(firstCallout);
    expect(secondCallout.textContent).toContain('Адаптер недоступен');
    expect(toasts.error).toHaveBeenCalledTimes(1);
    expect(toasts.error).toHaveBeenCalledWith('Адаптер недоступен');
  });
});
