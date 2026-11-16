import { Injectable, inject } from '@angular/core';
import { ApiClient } from '@shared/api/api-client';
import type {
  BulkRetractRequest,
  BulkRetractResult,
  CreateObservationRequest,
  CreateObservationResponse,
  RetractRequest,
  RetractResult,
} from '@shared/api/types';

@Injectable({ providedIn: 'root' })
export class ObservationsApi {
  private readonly api = inject(ApiClient);

  create(body: CreateObservationRequest): Promise<CreateObservationResponse> {
    return this.api.post<CreateObservationResponse>('/observations', body);
  }

  retract(id: string, body: RetractRequest): Promise<RetractResult> {
    return this.api.post<RetractResult>(`/observations/${encodeURIComponent(id)}/retract`, body);
  }

  retractBulk(body: BulkRetractRequest): Promise<BulkRetractResult> {
    return this.api.post<BulkRetractResult>('/observations/retract-bulk', body);
  }
}
