import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { AuthTokenStore } from './auth-token';
import { authInterceptor } from './auth.interceptor';

describe('authInterceptor', () => {
  let http: HttpClient;
  let tokens: AuthTokenStore;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    tokens = TestBed.inject(AuthTokenStore);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('добавляет Bearer, после 401 обновляет токен и повторяет запрос', async () => {
    tokens.set('old-token');
    const result = firstValueFrom(http.get('/api/v1/me'));

    const first = httpMock.expectOne('/api/v1/me');
    expect(first.request.headers.get('Authorization')).toBe('Bearer old-token');
    first.flush(
      { title: 'Нужен вход', status: 401 },
      { status: 401, statusText: 'Unauthorized' },
    );

    const refresh = httpMock.expectOne('/api/v1/auth/refresh');
    expect(refresh.request.method).toBe('POST');
    expect(refresh.request.withCredentials).toBe(true);
    expect(refresh.request.headers.get('X-Requested-With')).toBe('mayak');
    refresh.flush({ accessToken: 'fresh-token', expiresIn: 300 });
    await new Promise<void>((resolve) => setTimeout(resolve));

    const retry = httpMock.expectOne('/api/v1/me');
    expect(retry.request.headers.get('Authorization')).toBe('Bearer fresh-token');
    retry.flush({ id: 'u1', displayName: 'Ольга', roles: ['moderator'], permissions: [] });

    await expect(result).resolves.toMatchObject({ displayName: 'Ольга' });
    expect(tokens.token()).toBe('fresh-token');
  });

  it('при неуспешном refresh пробрасывает 401 и очищает токен', async () => {
    tokens.set('old-token');
    const result = firstValueFrom(http.get('/api/v1/me'));

    const first = httpMock.expectOne('/api/v1/me');
    first.flush(null, { status: 401, statusText: 'Unauthorized' });

    const refresh = httpMock.expectOne('/api/v1/auth/refresh');
    refresh.flush(null, { status: 401, statusText: 'Unauthorized' });

    await expect(result).rejects.toMatchObject({ status: 401 });
    expect(tokens.token()).toBeNull();
  });

  it('не повторяет запрос к /auth/', async () => {
    tokens.set('old-token');
    const result = firstValueFrom(http.post('/api/v1/auth/logout', null));

    const request = httpMock.expectOne('/api/v1/auth/logout');
    expect(request.request.headers.get('Authorization')).toBe('Bearer old-token');
    expect(request.request.withCredentials).toBe(true);
    expect(request.request.headers.get('X-Requested-With')).toBe('mayak');
    request.flush(null, { status: 401, statusText: 'Unauthorized' });

    await expect(result).rejects.toMatchObject({ status: 401 });
    httpMock.expectNone('/api/v1/auth/refresh');
  });
});
