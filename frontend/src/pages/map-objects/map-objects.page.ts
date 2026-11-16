import { Component, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { MapObjectEdit } from '@features/map-object-edit/map-object-edit';
import { toMapGeometry } from '@features/map-object-edit/map-object-form.model';
import { CatalogObjectsApi, ListCatalogObjectsQuery } from '@entities/map-object';
import { GroupsApi } from '@entities/object-group';
import { ObjectTypesApi } from '@entities/object-type';
import { SourcesApi } from '@entities/source';
import { errorText, toApiError } from '@shared/api/api-error';
import type { CoverageKind, MapObject, ObjectGroup, ObjectType } from '@shared/api/types';
import { SOURCE_TYPE_LABEL } from '@shared/lib/labels';
import type { MapGeometry, MapItem } from '@shared/map-adapter/map-adapter';
import { MapViewComponent } from '@shared/map-adapter/map-view';
import { EntityPicker, PickerLoader } from '@shared/ui/entity-picker';
import { Icon } from '@shared/ui/icon';
import { LoadMore } from '@shared/ui/load-more';
import { PageHead } from '@shared/ui/page-head';
import { SearchInput } from '@shared/ui/search-input';
import { ToastService } from '@shared/ui/toast';

const PAGE_SIZE = 30;

/** Фиксации на карте: список с фильтрами, карта и форма. */
@Component({
  selector: 'app-map-objects-page',
  imports: [
    MapObjectEdit,
    MapViewComponent,
    EntityPicker,
    Icon,
    LoadMore,
    PageHead,
    SearchInput,
  ],
  template: `
    <div class="page">
      <app-page-head
        title="Фиксации на карте"
        lead="Геозона или точка, к которой привязаны один или несколько источников данных."
      >
        <button class="btn btn--primary" type="button" (click)="create()">
          <app-icon name="plus" />Создать фиксацию
        </button>
      </app-page-head>

      <div class="card filters">
        <div class="row">
          <app-search-input class="grow" [(value)]="query" placeholder="Поиск по названию" />
          <select [value]="typeFilter()" (change)="typeFilter.set($any($event.target).value)" aria-label="Тип объекта">
            <option value="">Все типы</option>
            @for (type of objectTypes(); track type.id) { <option [value]="type.id">{{ type.name }}</option> }
          </select>
          <select [value]="groupFilter()" (change)="groupFilter.set($any($event.target).value)" aria-label="Группа">
            <option value="">Все группы</option>
            @for (group of groups(); track group.id) { <option [value]="group.id">{{ group.name }}</option> }
          </select>
          <select [value]="kindFilter()" (change)="kindFilter.set($any($event.target).value)" aria-label="Вид">
            <option value="">Все виды</option>
            <option value="point">Точка</option>
            <option value="polygon">Геозона</option>
          </select>
          <select [value]="activeFilter()" (change)="activeFilter.set($any($event.target).value)" aria-label="Активность">
            <option value="">Все</option>
            <option value="true">Активные</option>
            <option value="false">Неактивные</option>
          </select>
        </div>
        <div class="row">
          <app-entity-picker class="grow" [loader]="sourceLoader" [(value)]="sourceFilter" placeholder="Любой источник" />
        </div>
      </div>

      <div class="split">
        <div class="card card--flush mapbox">
          <app-map-view
            [items]="items()"
            [selectedId]="selectedMapId()"
            [focus]="focus()"
            [drawMode]="drawMode()"
            [drawInitial]="drawInitial()"
            [userLocation]="true"
            (selectItem)="select($event)"
            (drawChange)="onDrawn($event)"
            (locationError)="onLocationError($event)"
          />
        </div>

        @if (isNew()) {
          <app-map-object-edit
            [object]="null"
            [isNew]="true"
            [objectTypes]="objectTypes()"
            [groups]="groups()"
            (saved)="onSaved($event)"
            (removed)="onRemoved()"
            (closed)="close()"
            (undoRequest)="mapView()?.undoVertex()"
            (clearRequest)="mapView()?.clearDraft()"
          />
        } @else if (selected(); as object) {
          <app-map-object-edit
            [object]="object"
            [isNew]="false"
            [objectTypes]="objectTypes()"
            [groups]="groups()"
            (saved)="onSaved($event)"
            (removed)="onRemoved()"
            (closed)="close()"
            (undoRequest)="mapView()?.undoVertex()"
            (clearRequest)="mapView()?.clearDraft()"
          />
        } @else if (selectedId()) {
          <div class="card empty">Загрузка фиксации…</div>
        } @else {
          <div class="card empty">Выберите фиксацию на карте или в таблице, либо создайте новую.</div>
        }
      </div>

      <div class="card card--flush table-wrap">
        <table class="table">
          <thead>
            <tr>
              <th>Фиксация</th>
              <th>Вид</th>
              <th>Тип</th>
              <th>Группа</th>
              <th>Адрес</th>
              <th>Источники</th>
            </tr>
          </thead>
          <tbody>
            @for (object of objects(); track object.id) {
              <tr
                class="is-clickable"
                [class.is-active]="object.id === selectedId()"
                [class.is-muted]="!object.isActive"
                (click)="select(object.id)"
              >
                <td>
                  <strong>{{ object.name }}</strong>
                  @if (!object.isActive) { <small> · неактивна</small> }
                </td>
                <td><span class="chip">{{ coverageLabel(object.coverageKind) }}</span></td>
                <td>{{ typeName(object.objectTypeId) }}</td>
                <td>{{ groupName(object.groupId) }}</td>
                <td>{{ object.address || '—' }}</td>
                <td>
                  @if (object.sourceIds.length) {
                    <span class="chip"><app-icon name="link" />{{ object.sourceIds.length }}</span>
                  } @else {
                    <span class="chip chip--warn">нет источников — отметить нельзя</span>
                  }
                </td>
              </tr>
            } @empty {
              <tr>
                <td colspan="6">
                  <div class="empty">
                    {{ loading() ? 'Загрузка…' : 'Фиксаций нет. Измените фильтры или создайте новую.' }}
                  </div>
                </td>
              </tr>
            }
          </tbody>
        </table>
        <app-load-more [hasMore]="nextCursor() !== null" [loading]="loadingMore()" (more)="loadMore()" />
      </div>
    </div>`,
  styles: `
    .filters { display: grid; gap: 8px; }
    .filters select {
      height: var(--control-h);
      padding: 0 34px 0 10px;
      border: 1px solid var(--line);
      border-radius: var(--r-md);
      background-color: var(--surface);
    }
    .mapbox { height: 520px; border-radius: var(--r-lg); overflow: hidden; }
    .is-muted { opacity: 0.55; }
    .chip--warn { background: color-mix(in srgb, var(--st-none) 12%, var(--surface)); color: var(--st-none); }
  `,
})
export class MapObjectsPage {
  private readonly api = inject(CatalogObjectsApi);
  private readonly sourcesApi = inject(SourcesApi);
  private readonly objectTypesApi = inject(ObjectTypesApi);
  private readonly groupsApi = inject(GroupsApi);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly toasts = inject(ToastService);

  protected readonly mapView = viewChild(MapViewComponent);
  private readonly editor = viewChild(MapObjectEdit);

  protected readonly objects = signal<MapObject[]>([]);
  protected readonly objectTypes = signal<ObjectType[]>([]);
  protected readonly groups = signal<ObjectGroup[]>([]);
  protected readonly selected = signal<MapObject | null>(null);
  protected readonly selectedId = signal<string | null>(this.route.snapshot.queryParamMap.get('id'));
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly loadingMore = signal(false);
  protected readonly focus = signal<MapGeometry | null>(null);

  protected readonly query = signal('');
  protected readonly typeFilter = signal('');
  protected readonly groupFilter = signal('');
  protected readonly kindFilter = signal<'' | CoverageKind>('');
  protected readonly activeFilter = signal<'' | 'true' | 'false'>('');
  protected readonly sourceFilter = signal<string[]>([]);

  protected readonly isNew = computed(() => this.selectedId() === 'new');
  protected readonly selectedMapId = computed(() => {
    const id = this.selectedId();
    return id && id !== 'new' ? id : null;
  });

  protected readonly items = computed<MapItem[]>(() => {
    const result: MapItem[] = [];
    for (const object of this.objects()) {
      const geometry = toMapGeometry(object.geometry);
      if (!geometry) continue;
      result.push({
        id: object.id,
        name: object.name,
        geometry,
        tone: 'stale',
        icon: object.coverageKind === 'point' ? 'pin' : 'polygon',
        label: object.name,
        stale: !object.isActive,
      });
    }
    return result;
  });

  protected readonly drawMode = computed(() => this.editor()?.drawMode() ?? 'none');
  protected readonly drawInitial = computed(() => this.editor()?.drawInitial() ?? null);

  protected readonly sourceLoader: PickerLoader = async (query, cursor) => {
    const page = await this.sourcesApi.list({
      q: query || undefined,
      cursor: cursor ?? undefined,
      limit: 20,
    });
    return {
      items: page.items
        // «Сайт» доступен фиксациям неявно: привязывать его нельзя.
        .filter((source) => source.sourceType !== 'site')
        .map((source) => ({
          value: source.id,
          label: source.name,
          hint: `${SOURCE_TYPE_LABEL[source.sourceType]} · ${source.chatId}`,
        })),
      nextCursor: page.nextCursor,
    };
  };

  private loadSeq = 0;
  private focusedId: string | null = null;

  constructor() {
    effect(() => {
      const id = this.selectedId();
      void this.router.navigate([], {
        relativeTo: this.route,
        queryParams: { id },
        replaceUrl: true,
      });
    });

    effect(() => {
      this.query();
      this.typeFilter();
      this.groupFilter();
      this.kindFilter();
      this.activeFilter();
      this.sourceFilter();
      untracked(() => void this.load(true));
    });

    void this.loadLookups();
  }

  protected create(): void {
    this.focusedId = null;
    this.focus.set(null);
    this.selected.set(null);
    this.selectedId.set('new');
  }

  protected select(id: string): void {
    if (id === this.selectedId()) return;
    this.selectedId.set(id);
    this.focusedId = id;
    const found = this.objects().find((object) => object.id === id);
    this.selected.set(found ?? null);
    if (found) this.focus.set(toMapGeometry(found.geometry));
    else void this.loadOne(id);
  }

  protected close(): void {
    this.focusedId = null;
    this.focus.set(null);
    this.selected.set(null);
    this.selectedId.set(null);
  }

  protected loadMore(): void {
    const cursor = this.nextCursor();
    if (!cursor) return;
    void this.load(false);
  }

  protected onDrawn(geometry: MapGeometry | null): void {
    this.editor()?.onDrawn(geometry);
  }

  protected onLocationError(message: string): void {
    this.toasts.error(message);
  }

  protected onSaved(object: MapObject): void {
    this.selectedId.set(object.id);
    this.selected.set(object);
    this.focusedId = object.id;
    this.focus.set(toMapGeometry(object.geometry));
    this.objects.update((list) => {
      const index = list.findIndex((item) => item.id === object.id);
      if (index < 0) return [object, ...list];
      const next = [...list];
      next[index] = object;
      return next;
    });
    void this.load(true);
  }

  protected onRemoved(): void {
    const id = this.selected()?.id;
    if (id) this.objects.update((list) => list.filter((object) => object.id !== id));
    this.close();
  }

  protected coverageLabel(kind: CoverageKind): string {
    return kind === 'point' ? 'Точка' : 'Геозона';
  }

  protected typeName(id: string | null | undefined): string {
    return this.objectTypes().find((type) => type.id === id)?.name ?? '—';
  }

  protected groupName(id: string | null | undefined): string {
    return this.groups().find((group) => group.id === id)?.name ?? '—';
  }

  private async loadLookups(): Promise<void> {
    try {
      const [types, groups] = await Promise.all([this.objectTypesApi.list(), this.groupsApi.list()]);
      this.objectTypes.set(types);
      this.groups.set(groups);
    } catch (err) {
      this.toasts.fromError(err);
    }
  }

  private buildQuery(cursor: string | null): ListCatalogObjectsQuery {
    const query: ListCatalogObjectsQuery = { limit: PAGE_SIZE };
    const text = this.query().trim();
    if (text) query.q = text;
    if (this.typeFilter()) query.objectTypeId = this.typeFilter();
    if (this.groupFilter()) query.groupId = this.groupFilter();
    if (this.kindFilter()) query.kind = this.kindFilter() as CoverageKind;
    const sourceId = this.sourceFilter()[0];
    if (sourceId) query.sourceId = sourceId;
    if (this.activeFilter()) query.isActive = this.activeFilter() === 'true';
    if (cursor) query.cursor = cursor;
    return query;
  }

  private async load(reset: boolean): Promise<void> {
    if (!reset && (this.loading() || !this.nextCursor())) return;
    const seq = ++this.loadSeq;
    const cursor = reset ? null : this.nextCursor();
    this.loading.set(true);
    if (!reset) this.loadingMore.set(true);
    try {
      const page = await this.api.list(this.buildQuery(cursor));
      if (seq !== this.loadSeq) return;
      this.objects.set(reset ? page.items : [...this.objects(), ...page.items]);
      this.nextCursor.set(page.nextCursor);
      this.syncSelected();
    } catch (err) {
      if (seq !== this.loadSeq) return;
      const error = toApiError(err);
      this.toasts.error(errorText(error));
      if (reset) {
        this.objects.set([]);
        this.nextCursor.set(null);
      }
    } finally {
      if (seq === this.loadSeq) {
        this.loading.set(false);
        this.loadingMore.set(false);
      }
    }
  }

  private syncSelected(): void {
    const id = this.selectedId();
    if (!id || id === 'new') {
      this.selected.set(null);
      return;
    }
    const found = this.objects().find((object) => object.id === id);
    if (found) {
      this.selected.set(found);
      if (this.focusedId !== id) {
        this.focusedId = id;
        this.focus.set(toMapGeometry(found.geometry));
      }
      return;
    }
    if (this.selected()?.id !== id) void this.loadOne(id);
  }

  private async loadOne(id: string): Promise<void> {
    try {
      const object = await this.api.get(id);
      if (this.selectedId() !== id) return;
      this.selected.set(object);
      if (this.focusedId !== id) {
        this.focusedId = id;
        this.focus.set(toMapGeometry(object.geometry));
      }
    } catch (err) {
      const error = toApiError(err);
      if (error.status === 404) {
        this.toasts.error('Фиксация не найдена');
        this.close();
      } else {
        this.toasts.error(errorText(error));
      }
    }
  }
}
