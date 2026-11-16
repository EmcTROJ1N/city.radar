import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ApiClient, toHttpParams } from './api-client';

describe('toHttpParams', () => {
  it('сериализует массивы через запятую и пропускает пустые значения', () => {
    const params = toHttpParams({
      bbox: [37.8, 48, 38.1, 48.2],
      statuses: ['ok', 'none'],
      q: 'вода',
      zero: 0,
      flag: false,
      empty: '',
      missing: null,
      absent: undefined,
      list: [],
    });

    expect(params.get('bbox')).toBe('37.8,48,38.1,48.2');
    expect(params.get('statuses')).toBe('ok,none');
    expect(params.get('q')).toBe('вода');
    expect(params.get('zero')).toBe('0');
    expect(params.get('flag')).toBe('false');
    expect(params.keys().sort()).toEqual(['bbox', 'flag', 'q', 'statuses', 'zero']);
  });

  it('без параметров отдаёт пустой HttpParams', () => {
    expect(toHttpParams().keys()).toEqual([]);
  });
});

describe('ApiClient.getConditional', () => {
  let client: ApiClient;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    client = TestBed.inject(ApiClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('возвращает тело и ETag на 200', async () => {
    const result = client.getConditional<{ items: string[] }>(
      '/map/features',
      { bbox: [37.8, 48, 38.1, 48.2] },
      '"v1"',
    );

    const request = httpMock.expectOne((r) => r.url === '/api/v1/map/features');
    expect(request.request.headers.get('If-None-Match')).toBe('"v1"');
    expect(request.request.params.get('bbox')).toBe('37.8,48,38.1,48.2');
    request.flush({ items: ['a'] }, { statusText: 'OK', headers: { ETag: '"v2"' } });

    const response = await result;
    expect(response.notModified).toBe(false);
    if (!response.notModified) {
      expect(response.body).toEqual({ items: ['a'] });
      expect(response.etag).toBe('"v2"');
    }
  });

  it('на 304 сообщает notModified', async () => {
    const result = client.getConditional('/map/features', undefined, '"v1"');

    const request = httpMock.expectOne('/api/v1/map/features');
    expect(request.request.headers.get('If-None-Match')).toBe('"v1"');
    request.flush(null, { status: 304, statusText: 'Not Modified' });

    await expect(result).resolves.toEqual({ notModified: true });
  });
});
