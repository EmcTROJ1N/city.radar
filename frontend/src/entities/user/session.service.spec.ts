import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '@shared/api/api-error';
import { AuthTokenStore } from '@shared/api/auth-token';
import { P } from '@shared/lib/permissions';
import { SessionService } from './session.service';

const ME = {
  id: 'u1',
  displayName: 'Ольга',
  roles: ['moderator'],
  permissions: [P.mapRead, P.sourceManage],
};

describe('SessionService', () => {
  let session: SessionService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    session = TestBed.inject(SessionService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('считает права по permissions из /me', async () => {
    const loading = session.loadMe();
    httpMock.expectOne('/api/v1/me').flush(ME);
    await loading;

    expect(session.isAuthenticated()).toBe(true);
    expect(session.roleLabel()).toBe('Модератор');
    expect(session.has(P.sourceManage)).toBe(true);
    expect(session.has(P.rolesAssign)).toBe(false);
    expect(session.hasAny([P.rolesAssign, P.moderationProcess])).toBe(false);
    expect(session.hasAny([P.rolesAssign, P.sourceManage])).toBe(true);
  });

  it('при неуспешном refresh остаётся гостем и завершает инициализацию', async () => {
    const tokens = TestBed.inject(AuthTokenStore);
    vi.spyOn(tokens, 'refresh').mockResolvedValue(false);

    await session.init();

    expect(session.me()).toBeNull();
    expect(session.isAuthenticated()).toBe(false);
    expect(session.ready()).toBe(true);
  });

  it('сбрасывает профиль, когда access-токен пропал', async () => {
    const tokens = TestBed.inject(AuthTokenStore);
    tokens.set('token-1');

    const loading = session.loadMe();
    httpMock.expectOne('/api/v1/me').flush(ME);
    await loading;
    expect(session.me()).not.toBeNull();

    tokens.clear();
    TestBed.tick();

    expect(session.me()).toBeNull();
    expect(session.isAuthenticated()).toBe(false);
  });

  it('devLogin отправляет логин и пароль на сервер, сохраняет токен и загружает профиль', async () => {
    const tokens = TestBed.inject(AuthTokenStore);

    const loading = session.devLogin('admin', 'secret');

    const request = httpMock.expectOne('/api/v1/auth/dev-login');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ login: 'admin', password: 'secret' });
    request.flush({ accessToken: 'jwt-1', expiresIn: 600 });

    await new Promise((resolve) => setTimeout(resolve));
    expect(tokens.token()).toBe('jwt-1');
    httpMock.expectOne('/api/v1/me').flush(ME);

    await loading;
    expect(session.isAuthenticated()).toBe(true);
    expect(tokens.token()).toBe('jwt-1');
  });

  it('devLogin пробрасывает 401 и не выставляет профиль', async () => {
    const tokens = TestBed.inject(AuthTokenStore);

    const loading = session.devLogin('admin', 'wrong');

    const request = httpMock.expectOne('/api/v1/auth/dev-login');
    request.flush(
      { title: 'Неверный логин или пароль' },
      { status: 401, statusText: 'Unauthorized' },
    );

    await expect(loading).rejects.toBeInstanceOf(ApiError);
    expect(tokens.token()).toBeNull();
    expect(session.me()).toBeNull();
    expect(session.isAuthenticated()).toBe(false);
  });
});
