import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { MapApi } from './api';

describe('MapApi', () => {
  let api: MapApi;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(MapApi);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('шлёт If-None-Match, массивы через запятую и возвращает тело с ETag', async () => {
    const result = api.listFeatures(
      {
        bbox: '37.6,47.9,38.1,48.2',
        zoom: 12,
        categories: ['c1', 'c2'],
        kinds: ['point', 'polygon'],
      },
      '"v1"',
    );

    const request = httpMock.expectOne((r) => r.url === '/api/v1/map/features');
    expect(request.request.method).toBe('GET');
    expect(request.request.headers.get('If-None-Match')).toBe('"v1"');
    expect(request.request.params.get('bbox')).toBe('37.6,47.9,38.1,48.2');
    expect(request.request.params.get('zoom')).toBe('12');
    expect(request.request.params.get('categories')).toBe('c1,c2');
    expect(request.request.params.get('kinds')).toBe('point,polygon');

    const body = { generatedAt: '2026-11-15T09:30:00Z', items: [] };
    request.flush(body, { headers: { ETag: '"v2"' } });

    await expect(result).resolves.toEqual({ notModified: false, body, etag: '"v2"' });
  });

  it('на 304 сообщает notModified', async () => {
    const result = api.listFeatures({ bbox: '37.6,47.9,38.1,48.2' }, '"v1"');

    const request = httpMock.expectOne((r) => r.url === '/api/v1/map/features');
    expect(request.request.headers.get('If-None-Match')).toBe('"v1"');
    request.flush(null, { status: 304, statusText: 'Not Modified' });

    await expect(result).resolves.toEqual({ notModified: true });
  });
});
