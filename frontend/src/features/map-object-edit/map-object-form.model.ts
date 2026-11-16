import type { CoverageKind, Geometry, MapObject, MapObjectInput, ObjectType } from '@shared/api/types';
import type { LngLat, MapGeometry } from '@shared/map-adapter/map-adapter';
import { closeRing } from '@shared/map-adapter/geometry';

export interface MapObjectFormValue {
  name: string;
  coverageKind: CoverageKind;
  geometry: MapGeometry | null;
  objectTypeId: string | null;
  groupId: string | null;
  address: string;
  attributes: Record<string, unknown>;
  isActive: boolean;
  sourceIds: string[];
}

export interface SourceDiff {
  toLink: string[];
  toUnlink: string[];
}

export type AttributeKind = 'string' | 'number' | 'integer' | 'boolean' | 'enum';

export const COVERAGE: { value: CoverageKind; label: string }[] = [
  { value: 'polygon', label: 'Геозона' },
  { value: 'point', label: 'Точка' },
];

export interface AttributeField {
  key: string;
  label: string;
  kind: AttributeKind;
  options: { value: string; label: string }[];
}

export function emptyMapObjectForm(): MapObjectFormValue {
  return {
    name: '',
    coverageKind: 'polygon',
    geometry: null,
    objectTypeId: null,
    groupId: null,
    address: '',
    attributes: {},
    isActive: true,
    sourceIds: [],
  };
}

export function formFromObject(object: MapObject): MapObjectFormValue {
  return {
    name: object.name,
    coverageKind: object.coverageKind,
    geometry: toMapGeometry(object.geometry),
    objectTypeId: object.objectTypeId ?? null,
    groupId: object.groupId ?? null,
    address: object.address ?? '',
    attributes: { ...object.attributes },
    isActive: object.isActive,
    sourceIds: [...object.sourceIds],
  };
}

export function toMapGeometry(geometry: Geometry): MapGeometry | null {
  if (geometry.type === 'Point') {
    const [lon, lat] = geometry.coordinates;
    if (typeof lon !== 'number' || typeof lat !== 'number') return null;
    return { type: 'Point', coordinates: [lon, lat] };
  }
  const rings: LngLat[][] = [];
  for (const ring of geometry.coordinates) {
    const points: LngLat[] = [];
    for (const [lon, lat] of ring) {
      if (typeof lon === 'number' && typeof lat === 'number') points.push([lon, lat]);
    }
    if (points.length) rings.push(points);
  }
  return rings.length ? { type: 'Polygon', coordinates: rings } : null;
}

export function toApiGeometry(geometry: MapGeometry): Geometry {
  if (geometry.type === 'Point') {
    return { type: 'Point', coordinates: [geometry.coordinates[0], geometry.coordinates[1]] };
  }
  return {
    type: 'Polygon',
    coordinates: geometry.coordinates.map((ring) => closeRing(ring).map(([lon, lat]) => [lon, lat])),
  };
}

export function inputFromForm(
  value: MapObjectFormValue,
  options: { withSources?: boolean } = {},
): MapObjectInput {
  if (!value.geometry) throw new Error('Не задана геометрия фиксации');
  const body: MapObjectInput = {
    name: value.name.trim(),
    objectTypeId: value.objectTypeId ?? null,
    groupId: value.groupId ?? null,
    coverageKind: value.coverageKind,
    geometry: toApiGeometry(value.geometry),
    attributes: { ...value.attributes },
    isActive: value.isActive,
  };
  const address = value.address.trim();
  if (address) body.address = address;
  if (options.withSources !== false) body.sourceIds = [...new Set(value.sourceIds)];
  return body;
}

/** Что привязать и отвязать после update. */
export function sourceDiff(original: readonly string[], next: readonly string[]): SourceDiff {
  const before = new Set(original);
  const after = new Set(next);
  return {
    toLink: [...after].filter((id) => !before.has(id)),
    toUnlink: [...before].filter((id) => !after.has(id)),
  };
}

export function polygonVertexCount(geometry: MapGeometry | null): number {
  if (!geometry || geometry.type !== 'Polygon') return 0;
  const ring = geometry.coordinates[0] ?? [];
  if (ring.length > 1) {
    const first = ring[0]!;
    const last = ring[ring.length - 1]!;
    if (first[0] === last[0] && first[1] === last[1]) return ring.length - 1;
  }
  return ring.length;
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;
}

/** Поля формы атрибутов по JSON Schema типа; без `properties` — пусто (тогда JSON-текст). */
export function attributeFields(schema: unknown): AttributeField[] {
  const root = asRecord(schema);
  const properties = asRecord(root?.['properties']);
  if (!properties) return [];

  const fields: AttributeField[] = [];
  for (const [key, raw] of Object.entries(properties)) {
    const property = asRecord(raw);
    if (!property) continue;
    const enumValues = Array.isArray(property['enum']) ? property['enum'] : [];
    const rawType = property['type'];
    const type = Array.isArray(rawType)
      ? rawType.find((value) => typeof value === 'string')
      : rawType;
    const kind: AttributeKind = enumValues.length
      ? 'enum'
      : type === 'number'
        ? 'number'
        : type === 'integer'
          ? 'integer'
          : type === 'boolean'
            ? 'boolean'
            : 'string';
    fields.push({
      key,
      label: typeof property['title'] === 'string' ? property['title'] : key,
      kind,
      options: enumValues.map((value) => ({ value: String(value), label: String(value) })),
    });
  }
  return fields;
}

export function geometryMismatch(
  objectType: ObjectType | null | undefined,
  coverage: CoverageKind,
): boolean {
  const kind = objectType?.geometryKind;
  return Boolean(kind) && kind !== 'any' && kind !== coverage;
}
