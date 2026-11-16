import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, from, switchMap, throwError } from 'rxjs';
import { environment } from '@env/environment';
import { AuthTokenStore } from './auth-token';

function isAuthPath(url: string): boolean {
  return url.startsWith(`${environment.apiBase}/auth/`);
}

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.url.startsWith(environment.apiBase)) return next(req);

  const tokens = inject(AuthTokenStore);
  const token = tokens.token();
  const authPath = isAuthPath(req.url);

  let authReq = req;
  if (token) {
    authReq = authReq.clone({ setHeaders: { Authorization: `Bearer ${token}` } });
  }
  if (authPath) {
    authReq = authReq.clone({
      withCredentials: true,
      setHeaders: { 'X-Requested-With': 'mayak' },
    });
  }

  return next(authReq).pipe(
    catchError((err: unknown) => {
      if (err instanceof HttpErrorResponse && err.status === 401 && !authPath && token) {
        return from(tokens.refresh()).pipe(
          switchMap((refreshed) => {
            if (!refreshed) return throwError(() => err);
            const fresh = tokens.token();
            const retry = fresh
              ? req.clone({ setHeaders: { Authorization: `Bearer ${fresh}` } })
              : req;
            return next(retry);
          }),
        );
      }
      return throwError(() => err);
    }),
  );
};
