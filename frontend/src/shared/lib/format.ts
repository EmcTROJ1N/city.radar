/** «5 мин назад», «2 ч назад», «3 дн назад». */
function ago(ms: number, now = Date.now()): string {
  const min = Math.max(0, Math.round((now - ms) / 60000));
  if (min < 1) return 'только что';
  if (min < 60) return `${min} мин назад`;
  const h = Math.round(min / 60);
  if (h < 48) return `${h} ч назад`;
  return `${Math.round(h / 24)} дн назад`;
}

/** То же, что `ago`, но для ISO-даты из API. */
export function agoIso(iso: string, now = Date.now()): string {
  const ms = Date.parse(iso);
  if (Number.isNaN(ms)) return '';
  return ago(ms, now);
}

/** dd.MM.yyyy HH:mm в локальном времени. */
export function dateTime(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  const pad = (n: number) => String(n).padStart(2, '0');
  const datePart = `${pad(date.getDate())}.${pad(date.getMonth() + 1)}.${date.getFullYear()}`;
  return `${datePart} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

/** «45 мин», «6 ч», «1 д 2 ч». */
export function durationSec(sec: number): string {
  const total = Math.max(0, Math.round(sec));
  if (total < 60) return `${total} с`;
  const min = Math.round(total / 60);
  if (min < 60) return `${min} мин`;
  const hours = Math.floor(min / 60);
  const restMin = min % 60;
  if (hours < 24) return restMin ? `${hours} ч ${restMin} мин` : `${hours} ч`;
  const days = Math.floor(hours / 24);
  const restHours = hours % 24;
  return restHours ? `${days} д ${restHours} ч` : `${days} д`;
}
