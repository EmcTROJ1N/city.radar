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

const SITE: MapObjectCard = {
  ...CARD,
  sources: [
    {
      id: 'site-1',
      name: 'Сайт · Вода',
      sourceType: 'site',
      timeoutSeconds: 10800,
      statuses: [
        { id: 'site-ok', code: 'ok', name: 'Вода есть', color: 'ok', icon: 'drop' },
        { id: 'site-none', code: 'none', name: 'Воды нет', color: 'none', icon: 'cross' },
      ],
      lastObservation: null,
    },
    {
      id: 'src-1',
      name: 'Чат жильцов',
      sourceType: 'telegram_chat',
      timeoutSeconds: 3600,
      statuses: [
        { id: 'st-ok', code: 'ok', name: 'Вода есть', color: 'ok', icon: 'drop' },
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

  it('без источников объясняет, что у фиксации не задан тип', () => {
    has.mockReturnValue(true);
    const fixture = TestBed.createComponent(ReportStatus);
    fixture.componentRef.setInput('card', { ...CARD, sources: [] });
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('не задан тип');
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

  it('обычный пользователь отмечает по «Сайту» без выбора источника', async () => {
    has.mockImplementation((permission: string) => permission === 'observation.create');
    const fixture = TestBed.createComponent(ReportStatus);
    fixture.componentRef.setInput('card', SITE);
    fixture.detectChanges();

    // Селекта источника нет: «Сайт» выбран сам.
    expect(fixture.nativeElement.querySelector('#rs-source')).toBeNull();
    const tiles: HTMLButtonElement[] = Array.from(fixture.nativeElement.querySelectorAll('button.tile'));
    expect(tiles).toHaveLength(2);
    tiles[1]!.click();
    fixture.detectChanges();

    const form: HTMLFormElement = fixture.nativeElement.querySelector('form');
    form.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
    await fixture.whenStable();

    expect(create).toHaveBeenCalledWith({
      mapObjectId: 'obj-1',
      sourceId: 'site-1',
      sourceStatusId: 'site-none',
    });
  });

  it('модератору остаётся выбор источника', () => {
    has.mockReturnValue(true);
    const fixture = TestBed.createComponent(ReportStatus);
    fixture.componentRef.setInput('card', SITE);
    fixture.detectChanges();

    const select: HTMLSelectElement | null = fixture.nativeElement.querySelector('#rs-source');
    expect(select).not.toBeNull();
    expect(select!.options).toHaveLength(2);
  });

  it('без статусов у «Сайта» предлагает их настроить', () => {
    has.mockImplementation((permission: string) => permission === 'observation.create');
    const fixture = TestBed.createComponent(ReportStatus);
    fixture.componentRef.setInput('card', { ...SITE, sources: [{ ...SITE.sources[0]!, statuses: [] }] });
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('ещё не настроены');
  });
});
