import { Injectable, inject } from '@angular/core';
import { ApiClient } from '@shared/api/api-client';
import type {
  ApiPaths,
  ComplaintCreated,
  ComplaintRequest,
  MapFeaturesResponse,
  MapObject,
  MapObjectCard,
  MapObjectInput,
  MapObjectPage,
  ObservationPage,
} from '@shared/api/types';

export type ListFeaturesQuery = NonNullable<
  ApiPaths['/map/features']['get']['parameters']['query']
>;

export type ListCatalogObjectsQuery = NonNullable<
  ApiPaths['/catalog/objects']['get']['parameters']['query']
>;

@Injectable({ providedIn: 'root' })
export class MapApi {
  private readonly api = inject(ApiClient);

  listFeatures(query: ListFeaturesQuery, etag: string | null = null) {
    return this.api.getConditional<MapFeaturesResponse>('/map/features', query, etag);
  }

  getCard(id: string): Promise<MapObjectCard> {
    return this.api.get<MapObjectCard>(`/map/objects/${encodeURIComponent(id)}`);
  }

  listObservations(id: string, limit?: number, cursor?: string): Promise<ObservationPage> {
    return this.api.get<ObservationPage>(`/objects/${encodeURIComponent(id)}/observations`, {
      limit,
      cursor,
    });
  }

  complain(id: string, body: ComplaintRequest): Promise<ComplaintCreated> {
    return this.api.post<ComplaintCreated>(`/objects/${encodeURIComponent(id)}/complaints`, body);
  }
}

@Injectable({ providedIn: 'root' })
export class CatalogObjectsApi {
  private readonly api = inject(ApiClient);

  list(query: ListCatalogObjectsQuery = {}): Promise<MapObjectPage> {
    return this.api.get<MapObjectPage>('/catalog/objects', query);
  }

  get(id: string): Promise<MapObject> {
    return this.api.get<MapObject>(`/catalog/objects/${encodeURIComponent(id)}`);
  }

  create(body: MapObjectInput): Promise<MapObject> {
    return this.api.post<MapObject>('/catalog/objects', body);
  }

  update(id: string, body: MapObjectInput): Promise<MapObject> {
    return this.api.put<MapObject>(`/catalog/objects/${encodeURIComponent(id)}`, body);
  }

  remove(id: string): Promise<void> {
    return this.api.delete(`/catalog/objects/${encodeURIComponent(id)}`);
  }

  linkSource(id: string, sourceId: string): Promise<void> {
    return this.api.put<void>(
      `/catalog/objects/${encodeURIComponent(id)}/sources/${encodeURIComponent(sourceId)}`,
    );
  }

  unlinkSource(id: string, sourceId: string): Promise<void> {
    return this.api.delete(
      `/catalog/objects/${encodeURIComponent(id)}/sources/${encodeURIComponent(sourceId)}`,
    );
  }
}
