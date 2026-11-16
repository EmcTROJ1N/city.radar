/** Чистая трансформация стиля OpenFreeMap: только русские подписи, без границ и стран. */

export const RU_NAME_FIELD = 'name:ru';
export const ATTRIBUTION =
  '<a href="https://openfreemap.org" target="_blank">OpenFreeMap</a> © ' +
  '<a href="https://www.openmaptiles.org/" target="_blank">OpenMapTiles</a> © ' +
  '<a href="https://www.openstreetmap.org/copyright" target="_blank">участники OpenStreetMap</a>';

const ALLOWED_FIELDS = new Set([RU_NAME_FIELD, 'housenumber']);
const BOUNDARY_LAYER = /boundary|country/i;

/** Все поля, которые читает выражение подписи: `["get", field]` и `["has", field]`. */
function fieldRefs(expression, found = new Set()) {
  if (Array.isArray(expression)) {
    const [op, arg] = expression;
    if ((op === 'get' || op === 'has') && typeof arg === 'string') found.add(arg);
    for (const item of expression) fieldRefs(item, found);
  } else if (expression && typeof expression === 'object') {
    for (const value of Object.values(expression)) fieldRefs(value, found);
  }
  return found;
}

function labelField(textField) {
  const json = JSON.stringify(textField ?? null);
  if (json.includes('housenumber')) return 'keep';
  if (json.includes('"ref"')) return 'drop';
  return 'ru';
}

function rewriteLayer(layer) {
  const textField = layer.layout?.['text-field'];
  if (textField === undefined) return layer;
  const action = labelField(textField);
  if (action === 'keep') return layer;
  if (action === 'drop') return null;
  return { ...layer, layout: { ...layer.layout, 'text-field': ['coalesce', ['get', RU_NAME_FIELD], ''] } };
}

function assertRussianOnlyLabels(style) {
  for (const layer of style.layers) {
    const textField = layer.layout?.['text-field'];
    if (textField === undefined) continue;
    for (const field of fieldRefs(textField)) {
      if (ALLOWED_FIELDS.has(field)) continue;
      throw new Error(
        `Слой «${layer.id}»: подпись читает поле «${field}» — разрешены только ${RU_NAME_FIELD} и housenumber`,
      );
    }
  }
}

/**
 * Возвращает копию стиля с русскими подписями; исходный объект не меняется.
 * @param {object} style стиль MapLibre в формате JSON
 * @param {{ name: string }} meta название стиля
 */
export function transformStyle(style, { name }) {
  if (!style || !Array.isArray(style.layers)) throw new Error('Стиль без массива layers');

  const result = structuredClone(style);

  const layers = [];
  for (const layer of result.layers) {
    if (BOUNDARY_LAYER.test(String(layer.id))) continue;
    const rewritten = rewriteLayer(layer);
    if (rewritten) layers.push(rewritten);
  }
  result.layers = layers;
  result.name = name;

  for (const source of Object.values(result.sources ?? {})) {
    if (source?.type === 'vector') source.attribution = ATTRIBUTION;
  }

  assertRussianOnlyLabels(result);
  return result;
}
