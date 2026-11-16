import { Injectable, inject } from '@angular/core';
import { ApiClient } from '@shared/api/api-client';
import type {
  ApiPaths,
  ApproveCaseRequest,
  ModerationCase,
  ModerationCasePage,
  RejectCaseRequest,
} from '@shared/api/types';

export type ListModerationCasesQuery = NonNullable<
  ApiPaths['/moderation/cases']['get']['parameters']['query']
>;

@Injectable({ providedIn: 'root' })
export class ModerationApi {
  private readonly api = inject(ApiClient);

  list(query: ListModerationCasesQuery = {}): Promise<ModerationCasePage> {
    return this.api.get<ModerationCasePage>('/moderation/cases', query);
  }

  get(id: string): Promise<ModerationCase> {
    return this.api.get<ModerationCase>(`/moderation/cases/${encodeURIComponent(id)}`);
  }

  approve(id: string, body: ApproveCaseRequest): Promise<ModerationCase> {
    return this.api.post<ModerationCase>(
      `/moderation/cases/${encodeURIComponent(id)}/approve`,
      body,
    );
  }

  reject(id: string, body: RejectCaseRequest): Promise<ModerationCase> {
    return this.api.post<ModerationCase>(
      `/moderation/cases/${encodeURIComponent(id)}/reject`,
      body,
    );
  }
}
