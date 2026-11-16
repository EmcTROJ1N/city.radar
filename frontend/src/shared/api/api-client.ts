import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, firstValueFrom } from 'rxjs';
import { environment } from '@env/environment';
import { toApiError } from './api-error';

export type QueryParams = Record<
  string,
  string | number | boolean | readonly (string | number)[] | null | undefined
>;

export interface CursorPage<T> {
  items: T[];
  nextCursor: string | null;
}

/** Массивы — OpenAPI style=form, explode=false (через запятую). Пустые значения не передаются. */
export function toHttpParams(params?: QueryParams): HttpParams {
  let result = new HttpParams();
  if (!params) return result;
  for (const [key, value] of Object.entries(params)) {
    if (value === null || value === undefined || value === '') continue;
    if (Array.isArray(value)) {
      if (value.length === 0) continue;
      result = result.set(key, value.join(','));
    } else {
      result = result.set(key, String(value));
    }
  }
  return result;
}

@Injectable({ providedIn: 'root' })
export class ApiClient {
  private readonly http = inject(HttpClient);

  async get<T>(path: string, params?: QueryParams): Promise<T> {
    return this.run(this.http.get<T>(this.url(path), { params: toHttpParams(params) }));
  }

  async post<T>(path: string, body?: unknown, params?: QueryParams): Promise<T> {
    return this.run(this.http.post<T>(this.url(path), body ?? null, { params: toHttpParams(params) }));
  }

  async put<T>(path: string, body?: unknown): Promise<T> {
    return this.run(this.http.put<T>(this.url(path), body ?? null));
  }

  async delete<T = void>(path: string, body?: unknown): Promise<T> {
    return this.run(this.http.delete<T>(this.url(path), { body: body ?? null }));
  }

  async getConditional<T>(
    path: string,
    params: QueryParams | undefined,
    etag: string | null,
  ): Promise<{ notModified: true } | { notModified: false; body: T; etag: string | null }> {
    try {
      const response = await firstValueFrom(
        this.http.get<T>(this.url(path), {
          params: toHttpParams(params),
          observe: 'response',
          headers: etag ? { 'If-None-Match': etag } : {},
        }),
      );
      return { notModified: false, body: response.body as T, etag: response.headers.get('ETag') };
    } catch (err) {
      if (err instanceof HttpErrorResponse && err.status === 304) return { notModified: true };
      throw toApiError(err);
    }
  }

  private url(path: string): string {
    return `${environment.apiBase}${path}`;
  }

  private async run<T>(request: Observable<T>): Promise<T> {
    try {
      return await firstValueFrom(request);
    } catch (err) {
      throw toApiError(err);
    }
  }
}
