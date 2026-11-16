import { Injectable, inject } from '@angular/core';
import { ApiClient } from '@shared/api/api-client';
import type { StatusDefinition, StatusDefinitionInput } from '@shared/api/types';

@Injectable({ providedIn: 'root' })
export class StatusDefinitionsApi {
  private readonly api = inject(ApiClient);

  list(): Promise<StatusDefinition[]> {
    return this.api.get<StatusDefinition[]>('/catalog/statuses');
  }

  create(body: StatusDefinitionInput): Promise<StatusDefinition> {
    return this.api.post<StatusDefinition>('/catalog/statuses', body);
  }

  update(id: string, body: StatusDefinitionInput): Promise<StatusDefinition> {
    return this.api.put<StatusDefinition>(`/catalog/statuses/${encodeURIComponent(id)}`, body);
  }

  remove(id: string): Promise<void> {
    return this.api.delete(`/catalog/statuses/${encodeURIComponent(id)}`);
  }
}
