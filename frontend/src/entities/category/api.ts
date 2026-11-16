import { Injectable, inject } from '@angular/core';
import { ApiClient } from '@shared/api/api-client';
import type { Category, CategoryInput } from '@shared/api/types';

@Injectable({ providedIn: 'root' })
export class CategoriesApi {
  private readonly api = inject(ApiClient);

  list(): Promise<Category[]> {
    return this.api.get<Category[]>('/catalog/categories');
  }

  create(body: CategoryInput): Promise<Category> {
    return this.api.post<Category>('/catalog/categories', body);
  }

  update(id: string, body: CategoryInput): Promise<Category> {
    return this.api.put<Category>(`/catalog/categories/${encodeURIComponent(id)}`, body);
  }

  remove(id: string): Promise<void> {
    return this.api.delete(`/catalog/categories/${encodeURIComponent(id)}`);
  }
}
