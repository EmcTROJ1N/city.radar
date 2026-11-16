import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { SourcesApi } from './api';

describe('SourcesApi', () => {
  let api: SourcesApi;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(SourcesApi);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('сериализует query и обращается к /api/v1/sources', async () => {
    const result = api.list({ limit: 5, q: 'вод', isEnabled: true, sourceType: 'telegram_chat' });

    const request = httpMock.expectOne((r) => r.url === '/api/v1/sources');
    expect(request.request.method).toBe('GET');
    expect(request.request.params.get('limit')).toBe('5');
    expect(request.request.params.get('q')).toBe('вод');
    expect(request.request.params.get('isEnabled')).toBe('true');
    expect(request.request.params.get('sourceType')).toBe('telegram_chat');

    request.flush({ items: [], nextCursor: null });
    await expect(result).resolves.toEqual({ items: [], nextCursor: null });
  });

  it('кодирует идентификатор в пути', async () => {
    const result = api.get('id x');

    const request = httpMock.expectOne('/api/v1/sources/id%20x');
    request.flush({});
    await result;
  });
});
