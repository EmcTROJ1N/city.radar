import { TestBed } from '@angular/core/testing';
import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '@shared/api/api-error';
import type { MapFeature, MapFeaturesResponse, StatusView } from '@shared/api/types';
import { ToastService } from '@shared/ui/toast';
import { MapApi } from './api';
import { MapFeaturesStore } from './features.store';

const VIEW = {
  bbox: { minLon: 37.8, minLat: 48, maxLon: 38.1, maxLat: 48.2 },
  zoom: 11,
};

const FEATURE: MapFeature = {
  id: 'f1',
  name: 'Колонка на Ленинском',
  coverageKind: 'point',
  geometry: { type: 'Point', coordinates: [37.8, 48.01] },
  status: null,
};

const RESPONSE: MapFeaturesResponse = {
  generatedAt: '2026-11-15T09:30:00Z',
  items: [FEATURE],
};

const STATUS: StatusView = {
  sourceStatusId: 's1',
  code: 'ok',
  name: 'Работает',
  color: 'ok',
  icon: 'drop',
  freshness: 'fresh',
  observedAt: '2026-11-15T09:00:00Z',
  sourceType: 'max',
  observationCount: 1,
};

describe('MapFeaturesStore', () => {
  let api: { listFeatures: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    vi.useFakeTimers();
    localStorage.clear();
    api = { listFeatures: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        MapFeaturesStore,
        { provide: MapApi, useValue: api },
        {
          provide: ToastService,
          useValue: { error: vi.fn(), success: vi.fn(), info: vi.fn(), fromError: vi.fn(), dismiss: vi.fn() },
        },
      ],
    });
  });

  afterEach(() => {
    vi.useRealTimers();
    TestBed.resetTestingModule();
  });

  it('не передаёт groups, пока ни одна группа не скрыта', async () => {
    api.listFeatures.mockResolvedValue({ notModified: false, body: RESPONSE, etag: '"v1"' });
    const store = TestBed.inject(MapFeaturesStore);
    store.setKnownGroups(['g1', 'g2']);
    store.view.set(VIEW);

    await store.refresh();

    expect(api.listFeatures).toHaveBeenCalledTimes(1);
    const [query] = api.listFeatures.mock.calls[0]!;
    expect(query).not.toHaveProperty('groups');
  });

  it('передаёт видимые группы и none, когда часть групп скрыта', async () => {
    api.listFeatures.mockResolvedValue({ notModified: false, body: RESPONSE, etag: '"v1"' });
    const store = TestBed.inject(MapFeaturesStore);
    store.setKnownGroups(['g1', 'g2', 'g3']);
    store.filters.set({ kinds: ['point'], categories: [], statuses: [], hiddenGroups: ['g2'] });
    store.view.set(VIEW);

    await store.refresh();

    const [query] = api.listFeatures.mock.calls[0]!;
    expect(query.groups).toEqual(['g1', 'g3', 'none']);
    expect(query.kinds).toEqual(['point']);
  });

  it('на 304 не меняет данные и отправляет ETag прошлого ответа', async () => {
    api.listFeatures
      .mockResolvedValueOnce({ notModified: false, body: RESPONSE, etag: '"v1"' })
      .mockResolvedValueOnce({ notModified: true });
    const store = TestBed.inject(MapFeaturesStore);
    store.view.set(VIEW);

    await store.refresh();
    expect(store.items()).toEqual([FEATURE]);

    await store.refresh();

    expect(api.listFeatures).toHaveBeenLastCalledWith(expect.any(Object), '"v1"');
    expect(store.items()).toEqual([FEATURE]);
    expect(store.generatedAt()).toBe('2026-11-15T09:30:00Z');
  });

  it('при сетевой ошибке показывает кэш и помечает его', async () => {
    localStorage.setItem(
      'mayak.map.last',
      JSON.stringify({ items: [FEATURE], generatedAt: '2026-11-15T08:00:00Z' }),
    );
    api.listFeatures.mockRejectedValue(new ApiError(0, 'Нет связи с сервером'));

    const store = TestBed.inject(MapFeaturesStore);
    expect(store.fromCache()).toBe(true);
    expect(store.items()).toEqual([FEATURE]);

    store.view.set(VIEW);
    await store.refresh();

    expect(store.fromCache()).toBe(true);
    expect(store.items()).toEqual([FEATURE]);
    expect(store.error()).toBeNull();
  });

  it('после успешного ответа сбрасывает признак кэша и запоминает данные', async () => {
    localStorage.setItem(
      'mayak.map.last',
      JSON.stringify({ items: [], generatedAt: '2026-11-15T08:00:00Z' }),
    );
    api.listFeatures.mockResolvedValue({ notModified: false, body: RESPONSE, etag: '"v2"' });

    const store = TestBed.inject(MapFeaturesStore);
    store.view.set(VIEW);
    await store.refresh();

    expect(store.fromCache()).toBe(false);
    expect(store.items()).toEqual([FEATURE]);
    expect(localStorage.getItem('mayak.map.last')).toContain('Колонка на Ленинском');
  });

  it('applyStatus меняет статус только нужного элемента и не сбрасывает ETag', async () => {
    const second: MapFeature = { ...FEATURE, id: 'f2', name: 'Вторая колонка' };
    api.listFeatures.mockResolvedValue({
      notModified: false,
      body: { ...RESPONSE, items: [FEATURE, second] },
      etag: '"v1"',
    });
    const store = TestBed.inject(MapFeaturesStore);
    store.view.set(VIEW);
    await store.refresh();

    const before = store.items();
    store.applyStatus('f1', STATUS);

    expect(store.items()).not.toBe(before);
    expect(store.items()[0]!.status).toEqual(STATUS);
    expect(store.items()[1]!.status).toBeNull();
    expect(before[0]!.status).toBeNull();

    api.listFeatures.mockResolvedValueOnce({ notModified: true });
    await store.refresh();
    expect(api.listFeatures).toHaveBeenLastCalledWith(expect.any(Object), '"v1"');
  });

  it('refreshSoon делает один запрос через 1.5 с и переносит таймер при повторе', async () => {
    api.listFeatures.mockResolvedValue({ notModified: true });
    const store = TestBed.inject(MapFeaturesStore);
    store.view.set(VIEW);
    // Даём эффекту опроса выполнить стартовый запрос, дальше считаем только наши.
    await vi.advanceTimersByTimeAsync(0);
    api.listFeatures.mockClear();

    store.refreshSoon();
    store.refreshSoon();

    expect(api.listFeatures).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1400);
    expect(api.listFeatures).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(100);
    expect(api.listFeatures).toHaveBeenCalledTimes(1);
  });
});
