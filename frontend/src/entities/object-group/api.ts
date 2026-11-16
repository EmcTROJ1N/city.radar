import { Injectable, inject } from '@angular/core';
import { ApiClient } from '@shared/api/api-client';
import type { ObjectGroup, ObjectGroupInput } from '@shared/api/types';

@Injectable({ providedIn: 'root' })
export class GroupsApi {
  private readonly api = inject(ApiClient);

  list(): Promise<ObjectGroup[]> {
    return this.api.get<ObjectGroup[]>('/catalog/groups');
  }

  create(body: ObjectGroupInput): Promise<ObjectGroup> {
    return this.api.post<ObjectGroup>('/catalog/groups', body);
  }

  update(id: string, body: ObjectGroupInput): Promise<ObjectGroup> {
    return this.api.put<ObjectGroup>(`/catalog/groups/${encodeURIComponent(id)}`, body);
  }

  remove(id: string): Promise<void> {
    return this.api.delete(`/catalog/groups/${encodeURIComponent(id)}`);
  }
}
