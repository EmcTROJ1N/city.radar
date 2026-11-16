import { Injectable, inject } from '@angular/core';
import { ApiClient } from '@shared/api/api-client';
import type {
  ApiPaths,
  Source,
  SourceConnection,
  SourceCreate,
  SourceHealth,
  SourcePage,
  SourceStatus,
  SourceStatusInput,
  SourceUpdate,
} from '@shared/api/types';

export type ListSourcesQuery = NonNullable<ApiPaths['/sources']['get']['parameters']['query']>;

@Injectable({ providedIn: 'root' })
export class SourcesApi {
  private readonly api = inject(ApiClient);

  list(query: ListSourcesQuery = {}): Promise<SourcePage> {
    return this.api.get<SourcePage>('/sources', query);
  }

  get(id: string): Promise<Source> {
    return this.api.get<Source>(`/sources/${encodeURIComponent(id)}`);
  }

  create(body: SourceCreate): Promise<Source> {
    return this.api.post<Source>('/sources', body);
  }

  update(id: string, body: SourceUpdate): Promise<Source> {
    return this.api.put<Source>(`/sources/${encodeURIComponent(id)}`, body);
  }

  remove(id: string): Promise<void> {
    return this.api.delete(`/sources/${encodeURIComponent(id)}`);
  }

  enable(id: string): Promise<Source> {
    return this.api.post<Source>(`/sources/${encodeURIComponent(id)}/enable`);
  }

  disable(id: string): Promise<Source> {
    return this.api.post<Source>(`/sources/${encodeURIComponent(id)}/disable`);
  }

  health(id: string): Promise<SourceHealth> {
    return this.api.get<SourceHealth>(`/sources/${encodeURIComponent(id)}/health`);
  }

  listStatuses(id: string): Promise<SourceStatus[]> {
    return this.api.get<SourceStatus[]>(`/sources/${encodeURIComponent(id)}/statuses`);
  }

  createStatus(id: string, body: SourceStatusInput): Promise<SourceStatus> {
    return this.api.post<SourceStatus>(`/sources/${encodeURIComponent(id)}/statuses`, body);
  }

  updateStatus(id: string, statusId: string, body: SourceStatusInput): Promise<SourceStatus> {
    return this.api.put<SourceStatus>(
      `/sources/${encodeURIComponent(id)}/statuses/${encodeURIComponent(statusId)}`,
      body,
    );
  }

  removeStatus(id: string, statusId: string): Promise<void> {
    return this.api.delete(
      `/sources/${encodeURIComponent(id)}/statuses/${encodeURIComponent(statusId)}`,
    );
  }

  getConnection(id: string): Promise<SourceConnection> {
    return this.api.get<SourceConnection>(`/admin/sources/${encodeURIComponent(id)}/connection`);
  }

  updateConnection(id: string, body: SourceConnection): Promise<SourceConnection> {
    return this.api.put<SourceConnection>(
      `/admin/sources/${encodeURIComponent(id)}/connection`,
      body,
    );
  }
}
