import { Component, computed, inject, model, output, signal } from '@angular/core';
import { CategoriesApi } from '@entities/category';
import { GroupsApi } from '@entities/object-group';
import { StatusDefinitionsApi } from '@entities/status-definition';
import type { Category, ObjectGroup, StatusDefinition } from '@shared/api/types';
import { Tone } from '@shared/lib/tone';
import { Icon, IconName } from '@shared/ui/icon';
import { MultiSelect, SelectOption } from '@shared/ui/multi-select';
import { MapFilters as MapFilterState } from '@entities/map-object';

interface LegendItem {
  tone: Tone;
  icon: IconName;
  text: string;
}

const NO_GROUP = 'none';

/** Фильтры публичной карты: вид, категория, статус, группы. */
@Component({
  selector: 'app-map-filters',
  imports: [Icon, MultiSelect],
  template: `
    <section class="card stack" aria-labelledby="mf-title">
      <h2 id="mf-title" class="row"><app-icon name="filter" />Фильтры</h2>

      <div class="field">
        <label>На карте</label>
        <div class="segmented" role="group" aria-label="Что показывать">
          @for (k of kinds; track k.value) {
            <button type="button" [attr.aria-pressed]="kind() === k.value" (click)="setKind(k.value)">{{ k.label }}</button>
          }
        </div>
      </div>

      <div class="field">
        <label>Категория</label>
        <app-multi-select [options]="categoryOptions()" [selected]="filters().categories"
          (selectedChange)="patch({ categories: $event })" placeholder="Все категории" allLabel="Все категории"
          emptyText="Категории не загрузились" [showChips]="false" />
      </div>

      <div class="field">
        <label>Статус</label>
        <app-multi-select [options]="statusOptions()" [selected]="filters().statuses"
          (selectedChange)="patch({ statuses: $event })" placeholder="Все статусы" allLabel="Все статусы"
          emptyText="Справочник статусов пуст" [showChips]="false" />
      </div>

      <div class="field">
        <label>Группы</label>
        <app-multi-select [options]="groupOptions()" [selected]="visibleGroups()" (selectedChange)="setVisibleGroups($event)"
          [bulk]="true" allLabel="Все группы" emptyText="Групп пока нет" [showChips]="false" />
        <span class="hint">Новые группы появляются на карте сразу.</span>
      </div>

      <div class="legend">
        <small>Легенда</small>
        <div class="row">
          @for (item of legend; track item.tone) {
            <span class="status" [class]="'tone-' + item.tone" [class.is-stale]="item.tone === 'stale'">
              <app-icon [name]="item.icon" />{{ item.text }}
            </span>
          }
        </div>
      </div>

      <button class="btn btn--ghost btn--sm about" type="button" (click)="about.emit()">
        <app-icon name="info" />О достоверности данных
      </button>
    </section>`,
  styles: `
    .legend { display: grid; gap: 6px; }
    .legend .row { gap: 6px; }
    .about { justify-self: start; }
  `,
})
export class MapFilters {
  private readonly groupsApi = inject(GroupsApi);
  private readonly categoriesApi = inject(CategoriesApi);
  private readonly statusesApi = inject(StatusDefinitionsApi);

  readonly filters = model.required<MapFilterState>();
  readonly groupsLoaded = output<string[]>();
  readonly about = output<void>();

  protected readonly kinds = [
    { value: 'all', label: 'Всё' },
    { value: 'polygon', label: 'Зоны' },
    { value: 'point', label: 'Точки' },
  ] as const;

  protected readonly legend: LegendItem[] = [
    { tone: 'ok', icon: 'drop', text: 'Вода есть' },
    { tone: 'limited', icon: 'clock', text: 'Ограничения' },
    { tone: 'none', icon: 'cross', text: 'Воды нет' },
    { tone: 'schedule', icon: 'clock', text: 'По графику' },
    { tone: 'stale', icon: 'question', text: 'Неизвестно / устарело' },
  ];

  private readonly groups = signal<ObjectGroup[]>([]);
  private readonly categories = signal<Category[]>([]);
  private readonly statuses = signal<StatusDefinition[]>([]);

  protected readonly kind = computed<'all' | 'polygon' | 'point'>(() => {
    const kinds = this.filters().kinds;
    return kinds.length === 1 ? kinds[0]! : 'all';
  });

  protected readonly categoryOptions = computed<SelectOption[]>(() =>
    [...this.categories()]
      .sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name, 'ru'))
      .map((c) => ({ value: c.id, label: c.name })),
  );

  protected readonly statusOptions = computed<SelectOption[]>(() => {
    const seen = new Map<string, string>();
    for (const status of this.statuses()) if (!seen.has(status.code)) seen.set(status.code, status.name);
    return [...seen].map(([value, label]) => ({ value, label }));
  });

  protected readonly groupOptions = computed<SelectOption[]>(() => [
    { value: NO_GROUP, label: 'Без группы' },
    ...[...this.groups()]
      .sort((a, b) => a.name.localeCompare(b.name, 'ru'))
      .map((g) => ({ value: g.id, label: g.name })),
  ]);

  /** В списке отмечаются видимые группы, а хранится список скрытых. */
  protected readonly visibleGroups = computed(() => {
    const hidden = new Set(this.filters().hiddenGroups);
    return this.groupOptions()
      .map((o) => o.value)
      .filter((value) => !hidden.has(value));
  });

  constructor() {
    void this.loadLookups();
  }

  protected setKind(kind: 'all' | 'polygon' | 'point'): void {
    this.patch({ kinds: kind === 'all' ? [] : [kind] });
  }

  protected setVisibleGroups(visible: string[]): void {
    const hidden = this.groupOptions()
      .map((o) => o.value)
      .filter((value) => !visible.includes(value));
    this.patch({ hiddenGroups: hidden });
  }

  protected patch(part: Partial<MapFilterState>): void {
    this.filters.update((filters) => ({ ...filters, ...part }));
  }

  private async loadLookups(): Promise<void> {
    const [groups, categories, statuses] = await Promise.allSettled([
      this.groupsApi.list(),
      this.categoriesApi.list(),
      this.statusesApi.list(),
    ]);
    if (groups.status === 'fulfilled') {
      this.groups.set(groups.value);
      this.groupsLoaded.emit(groups.value.map((g) => g.id));
    }
    if (categories.status === 'fulfilled') this.categories.set(categories.value);
    if (statuses.status === 'fulfilled') this.statuses.set(statuses.value);
  }
}
