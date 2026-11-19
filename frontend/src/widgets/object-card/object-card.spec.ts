import { provideRouter } from '@angular/router';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MapApi } from '@entities/map-object';
import { ObservationsApi } from '@entities/observation';
import { SessionService } from '@entities/user';
import type {
  CreateObservationResponse,
  MapObjectCard,
  ObservationPage,
  PublicObservation,
  RetractResult,
} from '@shared/api/types';
import { ToastService } from '@shared/ui/toast';
import { ObjectCard } from './object-card';

const NOW = Date.now();
const iso = (minutesAgo: number): string => new Date(NOW - minutesAgo * 60_000).toISOString();

const ZONE: MapObjectCard = {
  id: 'zone-1',
  name: 'Мкрн Путиловка',
  coverageKind: 'polygon',
  geometry: { type: 'Polygon', coordinates: [[[37.7, 48.1], [37.71, 48.1], [37.71, 48.11], [37.7, 48.1]]] },
  address: null,
  status: null,
  sources: [
    {
      id: 'src-1',
      name: 'Чат жильцов мкрн Путиловка',
      sourceType: 'telegram_chat',
      timeoutSeconds: 21600,
      statuses: [{ id: 'st-none', code: 'none', name: 'Воды нет', color: 'none', icon: 'cross' }],
      lastObservation: null,
    },
  ],
};

const POINT: MapObjectCard = {
  ...ZONE,
  id: 'point-1',
  name: 'Колонка',
  coverageKind: 'point',
  geometry: { type: 'Point', coordinates: [37.7, 48.1] },
};

function observation(id: string, text: string, minutesAgo: number): PublicObservation {
  return {
    id,
    status: { id: 'st-none', code: 'none', name: 'Воды нет', color: 'none', icon: 'cross' },
    observedAt: iso(minutesAgo),
    expiresAt: iso(minutesAgo - 360),
    sourceType: 'telegram',
    sourceName: 'Чат жильцов мкрн Путиловка',
    text,
    address: 'Киевский проспект 10',
  };
}

const FIRST: ObservationPage = {
  items: [observation('o1', 'Киевский 10 — воды нет', 5), observation('o2', 'Намёткина 21 тоже сухо', 20)],
  nextCursor: 'c1',
};
const SECOND: ObservationPage = { items: [observation('o3', 'Экономическая 2а без воды', 40)], nextCursor: null };

function buttons(el: HTMLElement, root: ParentNode = el): HTMLButtonElement[] {
  return [...root.querySelectorAll('button')];
}

function button(root: ParentNode, text: string): HTMLButtonElement {
  const found = [...root.querySelectorAll('button')].find((b) => b.textContent?.replace(/\s+/g, ' ').trim().startsWith(text));
  if (!found) throw new Error(`Кнопка «${text}» не найдена`);
  return found;
}

/** Загрузка идёт обычными промисами: даём им выполниться и перерисовываем. */
async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  fixture.detectChanges();
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

describe('ObjectCard', () => {
  let getCard: ReturnType<typeof vi.fn>;
  let listObservations: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    getCard = vi.fn((id: string) => Promise.resolve(id === POINT.id ? POINT : ZONE));
    listObservations = vi.fn((_id: string, _limit: number, cursor?: string) =>
      Promise.resolve(cursor ? SECOND : FIRST));
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: MapApi, useValue: { getCard, listObservations } },
        { provide: ObservationsApi, useValue: { create: vi.fn(), retract: vi.fn() } },
        { provide: SessionService, useValue: { has: () => false, isAuthenticated: () => false } },
        {
          provide: ToastService,
          useValue: { error: vi.fn(), success: vi.fn(), info: vi.fn(), fromError: vi.fn(), dismiss: vi.fn() },
        },
      ],
    });
  });

  afterEach(() => TestBed.resetTestingModule());

  async function render(id = ZONE.id) {
    const fixture = TestBed.createComponent(ObjectCard);
    fixture.componentRef.setInput('objectId', id);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    const dialog = () => el.querySelector('dialog.details') as HTMLDialogElement;
    const report = () => el.querySelector('dialog.report') as HTMLDialogElement;
    return { fixture, el, dialog, report };
  }

  /** «Подробнее» в панели, затем — вкладка окна. */
  function openTab(el: HTMLElement, fixture: ComponentFixture<ObjectCard>, tab: string): void {
    button(el.querySelector('article')!, 'Подробнее').click();
    fixture.detectChanges();
    const tabButton = [...el.querySelectorAll('dialog [role=tab]')].find((t) => t.textContent?.trim() === tab);
    if (!tabButton) throw new Error(`Вкладка «${tab}» не найдена`);
    (tabButton as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  it('панель показывает статус, превью последней отметки и одну кнопку «Подробнее», но не ленту', async () => {
    const { el, dialog } = await render();

    const panel = el.querySelector('article') as HTMLElement;
    expect(panel.querySelector('.latest')?.textContent).toContain('Киевский 10 — воды нет');
    expect(button(panel, 'Подробнее')).toBeTruthy();
    const labels = buttons(panel).map((b) => b.textContent?.trim());
    expect(labels).not.toContain('Сводка');
    expect(labels.some((label) => label?.startsWith('Источники') || label === 'Отметки')).toBe(false);
    expect([...el.querySelectorAll('table.obs')].every((list) => dialog().contains(list))).toBe(true);
    expect(dialog().hasAttribute('open')).toBe(false);
  });

  it('«Подробнее» открывает окно на отметках; вкладок две — «Отметки» и «Источники»', async () => {
    const { fixture, el, dialog } = await render();

    button(el.querySelector('article')!, 'Подробнее').click();
    fixture.detectChanges();

    expect(dialog().hasAttribute('open')).toBe(true);
    expect([...dialog().querySelectorAll('[role=tab]')].map((t) => t.textContent?.trim())).toEqual(['Отметки', 'Источники']);
    expect(dialog().querySelector('[role=tab][aria-selected=true]')?.textContent?.trim()).toBe('Отметки');
    expect(dialog().querySelectorAll('table.obs tbody tr').length).toBe(2);
  });

  it('у зоны в окне — правило расчёта статуса, у точки — нет', async () => {
    const zone = await render();
    button(zone.el.querySelector('article')!, 'Подробнее').click();
    zone.fixture.detectChanges();
    expect(zone.dialog().textContent).toContain('Статус зоны — сводка отметок за час');

    const point = await render(POINT.id);
    button(point.el.querySelector('article')!, 'Подробнее').click();
    point.fixture.detectChanges();
    expect(point.dialog().textContent).not.toContain('Статус зоны');
  });

  it('адрес — строка внутри сообщения, а не отдельная колонка; без адреса — только текст', async () => {
    listObservations.mockResolvedValue({
      items: [observation('o1', 'Киевский 10 — воды нет', 5), { ...observation('o2', 'У нас снова еле течёт', 9), address: null }],
      nextCursor: null,
    });
    const { fixture, el, dialog } = await render();
    openTab(el, fixture, 'Отметки');

    const headers = [...dialog().querySelectorAll('table.obs thead th')].map((th) => th.textContent?.trim());
    expect(headers).not.toContain('Адрес');
    const [withAddress, withoutAddress] = [...dialog().querySelectorAll('table.obs tbody tr')];
    expect(withAddress.querySelector('.address')?.textContent).toContain('Киевский проспект 10');
    expect(withoutAddress.querySelector('.address')).toBeNull();
    expect(withoutAddress.textContent).not.toContain('—');
  });

  it('крестик закрывает окно', async () => {
    const { fixture, el, dialog } = await render();
    openTab(el, fixture, 'Отметки');

    (dialog().querySelector('button[aria-label="Закрыть окно"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(dialog().hasAttribute('open')).toBe(false);
  });

  it('смена объекта закрывает окно', async () => {
    const { fixture, el, dialog } = await render();
    openTab(el, fixture, 'Отметки');

    fixture.componentRef.setInput('objectId', POINT.id);
    fixture.detectChanges();
    await settle(fixture);

    expect(dialog().hasAttribute('open')).toBe(false);
  });

  it('после отзыва отметки окно остаётся открытым, лента перечитывается', async () => {
    const { fixture, el, dialog } = await render();
    openTab(el, fixture, 'Отметки');

    // Кнопка отзыва — отдельная фича; здесь проверяем реакцию карточки на её событие.
    (fixture.componentInstance as unknown as { onRetracted(result: RetractResult): void })
      .onRetracted({ observationId: 'o1', status: null } as unknown as RetractResult);
    await settle(fixture);

    expect(dialog().hasAttribute('open')).toBe(true);
    expect(listObservations).toHaveBeenCalledTimes(2);
  });

  it('без отметок — пустое состояние в панели и в окне', async () => {
    listObservations.mockResolvedValue({ items: [], nextCursor: null });
    const { fixture, el, dialog } = await render();

    expect(el.querySelector('article .latest')?.textContent).toContain('Пока нет отметок');
    openTab(el, fixture, 'Отметки');
    expect(dialog().querySelector('table.obs')?.textContent).toContain('Пока нет отметок');
  });

  it('панель — список «подпись — значение», без дубля предупреждения о достоверности', async () => {
    const { el } = await render();
    const panel = el.querySelector('article') as HTMLElement;

    const terms = [...panel.querySelectorAll('dl.kv dt')].map((dt) => dt.textContent?.trim());
    expect(terms).toEqual(expect.arrayContaining(['Статус', 'За час', 'Последнее', 'Адрес', 'Сообщение']));
    expect(panel.textContent).not.toContain('достоверность не гарантируется');
    expect(buttons(panel).map((b) => b.textContent?.trim())).not.toContain('О достоверности данных');
  });

  it('форма отметки спрятана в окно «Отметить статус»', async () => {
    const { fixture, el, report } = await render();
    const panel = el.querySelector('article') as HTMLElement;
    expect(report().hasAttribute('open')).toBe(false);
    expect(panel.querySelector('app-report-status')).toBeNull();

    button(panel, 'Отметить статус').click();
    fixture.detectChanges();

    expect(report().hasAttribute('open')).toBe(true);
    expect(report().textContent).toContain('войдите');
  });

  it('после успешной отметки окно «Отметить статус» закрывается', async () => {
    const { fixture, el, report } = await render();
    button(el.querySelector('article')!, 'Отметить статус').click();
    fixture.detectChanges();

    (fixture.componentInstance as unknown as { onReported(response: CreateObservationResponse): void })
      .onReported({ observation: null, status: null } as unknown as CreateObservationResponse);
    fixture.detectChanges();

    expect(report().hasAttribute('open')).toBe(false);
  });

  it('источники в окне — таблица, строка на источник', async () => {
    const { fixture, el, dialog } = await render();
    openTab(el, fixture, 'Источники');

    const rows = dialog().querySelectorAll('table.sources tbody tr');
    expect(rows.length).toBe(1);
    expect(rows[0].textContent).toContain('Чат жильцов мкрн Путиловка');
  });

  it('«Показать ещё» в окне догружает отметки', async () => {
    const { fixture, el, dialog } = await render();
    openTab(el, fixture, 'Отметки');

    button(dialog(), 'Показать ещё').click();
    await settle(fixture);

    expect(listObservations).toHaveBeenLastCalledWith(ZONE.id, 20, 'c1');
    expect(dialog().querySelectorAll('table.obs tbody tr').length).toBe(3);
  });
});
