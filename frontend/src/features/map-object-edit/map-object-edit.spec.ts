import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { CatalogObjectsApi } from '@entities/map-object';
import { SourcesApi } from '@entities/source';
import type { MapObject, ObjectType } from '@shared/api/types';
import { ConfirmService } from '@shared/ui/confirm';
import { ToastService } from '@shared/ui/toast';
import { MapObjectEdit } from './map-object-edit';

const TYPE: ObjectType = {
  id: 'type-1',
  categoryId: 'category-1',
  code: 'column',
  name: 'Колонка',
  geometryKind: 'any',
};

const OBJECT: MapObject = {
  id: 'obj-1',
  name: 'Колонка на Ленинском',
  objectTypeId: 'type-1',
  groupId: null,
  coverageKind: 'point',
  geometry: { type: 'Point', coordinates: [37.8, 48.01] },
  address: 'пр. Ленинский, 12',
  isActive: true,
  sourceIds: ['src-old'],
};

describe('MapObjectEdit', () => {
  let api: {
    create: ReturnType<typeof vi.fn>;
    update: ReturnType<typeof vi.fn>;
    remove: ReturnType<typeof vi.fn>;
    linkSource: ReturnType<typeof vi.fn>;
    unlinkSource: ReturnType<typeof vi.fn>;
  };
  let sources: {
    list: ReturnType<typeof vi.fn>;
    get: ReturnType<typeof vi.fn>;
  };
  let toasts: { success: ReturnType<typeof vi.fn>; error: ReturnType<typeof vi.fn>; fromError: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    api = {
      create: vi.fn().mockResolvedValue({ ...OBJECT, id: 'obj-new' }),
      update: vi.fn().mockResolvedValue(OBJECT),
      remove: vi.fn().mockResolvedValue(undefined),
      linkSource: vi.fn().mockResolvedValue(undefined),
      unlinkSource: vi.fn().mockResolvedValue(undefined),
    };
    sources = {
      list: vi.fn().mockResolvedValue({ items: [], nextCursor: null }),
      get: vi.fn().mockResolvedValue({
        id: 'src-old',
        name: 'Старый источник',
        sourceType: 'telegram_chat',
        chatId: '@old',
      }),
    };
    toasts = { success: vi.fn(), error: vi.fn(), fromError: vi.fn() };

    TestBed.configureTestingModule({
      providers: [
        { provide: CatalogObjectsApi, useValue: api },
        { provide: SourcesApi, useValue: sources },
        { provide: ToastService, useValue: toasts },
        { provide: ConfirmService, useValue: { ask: vi.fn().mockResolvedValue({ confirmed: true, reason: '' }) } },
      ],
    });
  });

  function createEditor(object: MapObject | null, isNew: boolean): MapObjectEdit {
    const fixture = TestBed.createComponent(MapObjectEdit);
    fixture.componentRef.setInput('object', object);
    fixture.componentRef.setInput('isNew', isNew);
    fixture.componentRef.setInput('objectTypes', [TYPE]);
    fixture.componentRef.setInput('groups', []);
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  it('при редактировании обновляет фиксацию и синхронизирует источники по разнице', async () => {
    const editor = createEditor(OBJECT, false);
    editor.sourceIds.set(['src-new']);

    await editor.save();

    expect(api.update).toHaveBeenCalledTimes(1);
    expect(api.update).toHaveBeenCalledWith('obj-1', expect.not.objectContaining({ sourceIds: expect.anything() }));
    expect(api.unlinkSource).toHaveBeenCalledTimes(1);
    expect(api.unlinkSource).toHaveBeenCalledWith('obj-1', 'src-old');
    expect(api.linkSource).toHaveBeenCalledTimes(1);
    expect(api.linkSource).toHaveBeenCalledWith('obj-1', 'src-new');
    expect(toasts.success).toHaveBeenCalledWith('Фиксация сохранена');
  });

  it('не трогает источники, если выбор не менялся', async () => {
    const editor = createEditor(OBJECT, false);

    await editor.save();

    expect(api.update).toHaveBeenCalledTimes(1);
    expect(api.unlinkSource).not.toHaveBeenCalled();
    expect(api.linkSource).not.toHaveBeenCalled();
  });

  it('при создании передаёт sourceIds в теле запроса', async () => {
    const editor = createEditor(null, true);
    editor.form.controls.name.setValue('Новая колонка');
    editor.form.controls.objectTypeId.setValue('type-1');
    editor.coverageKind.set('point');
    editor.geometry.set({ type: 'Point', coordinates: [37.8, 48.01] });
    editor.sourceIds.set(['src-new']);

    await editor.save();

    expect(api.create).toHaveBeenCalledTimes(1);
    expect(api.create).toHaveBeenCalledWith(expect.objectContaining({ sourceIds: ['src-new'] }));
    expect(api.linkSource).not.toHaveBeenCalled();
    expect(api.unlinkSource).not.toHaveBeenCalled();
  });

  it('без типа объекта не сохраняет и показывает ошибку у поля', async () => {
    const fixture = TestBed.createComponent(MapObjectEdit);
    fixture.componentRef.setInput('object', null);
    fixture.componentRef.setInput('isNew', true);
    fixture.componentRef.setInput('objectTypes', [TYPE]);
    fixture.componentRef.setInput('groups', []);
    fixture.detectChanges();
    const editor = fixture.componentInstance;
    editor.form.controls.name.setValue('Без типа');
    editor.coverageKind.set('point');
    editor.geometry.set({ type: 'Point', coordinates: [37.8, 48.01] });

    await editor.save();
    fixture.detectChanges();

    expect(api.create).not.toHaveBeenCalled();
    const select = (fixture.nativeElement as HTMLElement).querySelector('#mo-type') as HTMLSelectElement;
    expect(select.required).toBe(true);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Выберите тип объекта');
  });

  it('на 422 показывает ошибку формы без тоста', async () => {
    const { ApiError } = await import('@shared/api/api-error');
    api.update.mockRejectedValue(new ApiError(422, 'Ошибка валидации', null, { name: 'Недопустимое название' }));
    const editor = createEditor(OBJECT, false);

    await editor.save();

    expect(editor.apiError()?.fieldErrors['name']).toBe('Недопустимое название');
    expect(toasts.fromError).not.toHaveBeenCalled();
  });
});
