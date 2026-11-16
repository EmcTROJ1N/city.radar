import { provideRouter } from '@angular/router';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ObservationsApi } from '@entities/observation';
import { SessionService } from '@entities/user';
import type { MapObjectCard } from '@shared/api/types';
import { ToastService } from '@shared/ui/toast';
import { ReportStatus } from './report-status';

const CARD: MapObjectCard = {
  id: 'obj-1',
  name: 'Колонка на Ленинском',
  coverageKind: 'point',
  geometry: { type: 'Point', coordinates: [37.8, 48.01] },
  address: 'пр. Ленинский, 12',
  status: null,
  attributes: {},
  sources: [
    {
      id: 'src-1',
      name: 'Чат жильцов',
      sourceType: 'telegram_chat',
      timeoutSeconds: 3600,
      statuses: [
        { id: 'st-ok', code: 'ok', name: 'Вода есть', color: 'ok', icon: 'drop' },
        { id: 'st-none', code: 'none', name: 'Воды нет', color: 'none', icon: 'cross' },
      ],
      lastObservation: null,
    },
  ],
};

describe('ReportStatus', () => {
  let create: ReturnType<typeof vi.fn>;
  let has: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    create = vi.fn().mockResolvedValue({ observation: null, status: null });
    has = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: ObservationsApi, useValue: { create } },
        { provide: SessionService, useValue: { has } },
        {
          provide: ToastService,
          useValue: { error: vi.fn(), success: vi.fn(), info: vi.fn(), fromError: vi.fn(), dismiss: vi.fn() },
        },
      ],
    });
  });

  it('без права observation.create показывает приглашение войти', () => {
    has.mockReturnValue(false);
    const fixture = TestBed.createComponent(ReportStatus);
    fixture.componentRef.setInput('card', CARD);
    fixture.detectChanges();

    expect(has).toHaveBeenCalledWith('observation.create');
    expect(fixture.nativeElement.textContent).toContain('Чтобы отметить статус, войдите');
    expect(fixture.nativeElement.querySelector('form')).toBeNull();
  });

  it('без источников объясняет, почему отметить нельзя', () => {
    has.mockReturnValue(true);
    const fixture = TestBed.createComponent(ReportStatus);
    fixture.componentRef.setInput('card', { ...CARD, sources: [] });
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('нет источников');
  });

  it('отправляет выбранный статус через ObservationsApi.create', async () => {
    has.mockReturnValue(true);
    const fixture = TestBed.createComponent(ReportStatus);
    fixture.componentRef.setInput('card', CARD);
    fixture.detectChanges();

    const tiles: HTMLButtonElement[] = Array.from(fixture.nativeElement.querySelectorAll('button.tile'));
    expect(tiles).toHaveLength(2);
    tiles[1]!.click();
    fixture.detectChanges();

    const form: HTMLFormElement = fixture.nativeElement.querySelector('form');
    form.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
    await fixture.whenStable();

    expect(create).toHaveBeenCalledTimes(1);
    expect(create).toHaveBeenCalledWith({
      mapObjectId: 'obj-1',
      sourceId: 'src-1',
      sourceStatusId: 'st-none',
    });
  });
});
