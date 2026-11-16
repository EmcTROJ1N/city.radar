import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ModerationApi } from './api';

describe('ModerationApi', () => {
  let api: ModerationApi;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(ModerationApi);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('принимает кейс через POST с телом', async () => {
    const result = api.approve('case-1', { sourceStatusId: 'status-1' });

    const request = httpMock.expectOne('/api/v1/moderation/cases/case-1/approve');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ sourceStatusId: 'status-1' });

    const body = {
      id: 'case-1',
      kind: 'low_confidence',
      state: 'approved',
      createdAt: '2026-11-15T09:30:00Z',
    };
    request.flush(body);
    await expect(result).resolves.toEqual(body);
  });

  it('фильтрует очередь по виду и состоянию', async () => {
    const result = api.list({ kind: 'complaint', state: 'open', limit: 10 });

    const request = httpMock.expectOne((r) => r.url === '/api/v1/moderation/cases');
    expect(request.request.params.get('kind')).toBe('complaint');
    expect(request.request.params.get('state')).toBe('open');
    expect(request.request.params.get('limit')).toBe('10');

    request.flush({ items: [], nextCursor: null });
    await expect(result).resolves.toEqual({ items: [], nextCursor: null });
  });
});
