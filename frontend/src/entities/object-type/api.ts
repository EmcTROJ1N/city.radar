import { Injectable, inject } from '@angular/core';
import { ApiClient } from '@shared/api/api-client';
import type { ApiPaths, ObjectType, ObjectTypeInput } from '@shared/api/types';

export type ListObjectTypesQuery = NonNullable<
  ApiPaths['/catalog/object-types']['get']['parameters']['query']
>;

@Injectable({ providedIn: 'root' })
export class ObjectTypesApi {
  private readonly api = inject(ApiClient);

  list(query: ListObjectTypesQuery = {}): Promise<ObjectType[]> {
    return this.api.get<ObjectType[]>('/catalog/object-types', query);
  }

  create(body: ObjectTypeInput): Promise<ObjectType> {
    return this.api.post<ObjectType>('/catalog/object-types', body);
  }

  update(id: string, body: ObjectTypeInput): Promise<ObjectType> {
    return this.api.put<ObjectType>(`/catalog/object-types/${encodeURIComponent(id)}`, body);
  }

  remove(id: string): Promise<void> {
    return this.api.delete(`/catalog/object-types/${encodeURIComponent(id)}`);
  }
}
