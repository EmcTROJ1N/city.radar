import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { CategoriesApi } from '@entities/category';
import { SourcesApi } from '@entities/source';
import { StatusDefinitionsApi } from '@entities/status-definition';
import type { Source } from '@shared/api/types';
import { ConfirmService } from '@shared/ui/confirm';
import { ToastService } from '@shared/ui/toast';
import { SourcesPage } from './sources.page';

const SOURCE: Source = {
  id: 'src-1',
  name: 'Чат жильцов Калининского',
  sourceType: 'telegram_chat',
  chatId: '@kalininsky_water',
  timeoutSeconds: 21_600,
  confidenceThreshold: null,
  prefilter: null,
  userPrompt: 'Ищи все упоминания про подачу воды.',
  linkByText: true,
  isEnabled: true,
  state: 'ok',
  lastMessageAt: '2026-11-15T09:12:00Z',
  createdAt: '2026-11-01T08:00:00Z',
  statuses: [
    {
      id: 'st-1',
      code: 'ok',
      name: 'Вода есть',
      description: 'Подача воды идёт',
      color: 'ok',
      icon: 'drop',
      sortOrder: 1,
      statusDefinitionId: null,
    },
  ],
};

describe('SourcesPage', () => {
  let list: ReturnType<typeof vi.fn>;
  let health: ReturnType<typeof vi.fn>;

  const settle = async (): Promise<void> => {
    await new Promise((resolve) => setTimeout(resolve));
  };

  beforeEach(() => {
    list = vi.fn().mockResolvedValue({ items: [SOURCE], nextCursor: null });
    health = vi.fn().mockResolvedValue({
      state: 'ok',
      lastMessageAt: null,
      floodWaitUntil: null,
      lastError: null,
    });
    TestBed.configureTestingModule({
      providers: [
        {
          provide: SourcesApi,
          useValue: { list, get: vi.fn(), enable: vi.fn(), disable: vi.fn(), remove: vi.fn(), health },
        },
        { provide: StatusDefinitionsApi, useValue: { list: vi.fn().mockResolvedValue([]) } },
        { provide: CategoriesApi, useValue: { list: vi.fn().mockResolvedValue([]) } },
        {
          provide: ToastService,
          useValue: { success: vi.fn(), error: vi.fn(), info: vi.fn(), fromError: vi.fn(), dismiss: vi.fn() },
        },
        { provide: ConfirmService, useValue: { ask: vi.fn() } },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({}) } } },
        { provide: Router, useValue: { navigate: vi.fn().mockResolvedValue(true) } },
      ],
    });
  });

  it('загружает источники при старте и показывает строки', async () => {
    const fixture = TestBed.createComponent(SourcesPage);
    fixture.detectChanges();
    await fixture.whenStable();
    await settle();
    fixture.detectChanges();

    expect(list).toHaveBeenCalledTimes(1);
    expect(list).toHaveBeenCalledWith(expect.objectContaining({ limit: 50 }));

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Чат жильцов Калининского');
    expect(text).toContain('@kalininsky_water');
    // Статусы в списке — компактные точки, подписи показываются по наведению.
    const dots = fixture.nativeElement.querySelectorAll('td .statuses__dot');
    expect(dots).toHaveLength(1);

    (fixture.nativeElement.querySelector('.statuses') as HTMLElement).dispatchEvent(
      new MouseEvent('mouseenter'),
    );
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Вода есть');

    expect(fixture.nativeElement.querySelectorAll('tbody tr')).toHaveLength(1);
  });

  it('по клику на строку открывает форму, статусы и состояние источника', async () => {
    const fixture = TestBed.createComponent(SourcesPage);
    fixture.detectChanges();
    await fixture.whenStable();
    await settle();
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('tbody tr') as HTMLTableRowElement).click();
    fixture.detectChanges();
    await settle();
    fixture.detectChanges();

    expect(health).toHaveBeenCalledWith('src-1');
    expect(fixture.nativeElement.querySelector('app-source-form form')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('app-source-statuses')).not.toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Состояние');
    expect(fixture.nativeElement.textContent).toContain('Статусы источника');
  });

  it('показывает пустое состояние, когда источников нет', async () => {
    list.mockResolvedValue({ items: [], nextCursor: null });
    const fixture = TestBed.createComponent(SourcesPage);
    fixture.detectChanges();
    await fixture.whenStable();
    await settle();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Пока нет источников');
  });

  it('у «Сайта» системная плашка с категорией и нет удаления', async () => {
    const site: Source = {
      ...SOURCE,
      id: 'site-1',
      name: 'Сайт · Вода',
      sourceType: 'site',
      categoryId: 'cat-water',
      chatId: '',
      userPrompt: '',
      linkByText: false,
      state: 'unknown',
      lastMessageAt: null,
    };
    const categories = TestBed.inject(CategoriesApi) as unknown as {
      list: ReturnType<typeof vi.fn>;
    };
    categories.list.mockResolvedValue([{ id: 'cat-water', name: 'Вода' }]);
    list.mockResolvedValue({ items: [site], nextCursor: null });

    const fixture = TestBed.createComponent(SourcesPage);
    fixture.detectChanges();
    await fixture.whenStable();
    await settle();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Системный · Вода');

    (fixture.nativeElement.querySelector('tbody tr') as HTMLTableRowElement).click();
    fixture.detectChanges();
    await settle();
    fixture.detectChanges();

    // Форма системного источника не показывает чат, промпт и удаление.
    expect(fixture.nativeElement.querySelector('#sf-chat')).toBeNull();
    expect(fixture.nativeElement.querySelector('#sf-prompt')).toBeNull();
    expect(fixture.nativeElement.textContent).not.toContain('Удалить');
  });
});
