import { HttpClient, HttpBackend } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { environment } from '@env/environment';
import type { TokenResponse } from './types';

@Injectable({ providedIn: 'root' })
export class AuthTokenStore {
  readonly token = signal<string | null>(null);

  private readonly backend = inject(HttpBackend);
  // Без interceptor: refresh не должен сам пытаться обновить токен.
  private readonly http = new HttpClient(this.backend);
  private inFlight: Promise<boolean> | null = null;

  set(token: string): void {
    this.token.set(token);
  }

  clear(): void {
    this.token.set(null);
  }

  refresh(): Promise<boolean> {
    if (this.inFlight) return this.inFlight;
    this.inFlight = this.doRefresh().finally(() => {
      this.inFlight = null;
    });
    return this.inFlight;
  }

  private async doRefresh(): Promise<boolean> {
    try {
      const response = await firstValueFrom(
        this.http.post<TokenResponse>(`${environment.apiBase}/auth/refresh`, null, {
          withCredentials: true,
          headers: { 'X-Requested-With': 'mayak' },
        }),
      );
      if (!response?.accessToken) {
        this.clear();
        return false;
      }
      this.set(response.accessToken);
      return true;
    } catch {
      this.clear();
      return false;
    }
  }
}
