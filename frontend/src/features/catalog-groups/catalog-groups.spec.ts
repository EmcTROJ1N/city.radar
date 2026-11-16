import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { CategoriesApi } from '@entities/category';
import { GroupsApi } from '@entities/object-group';
import type { ObjectGroup } from '@shared/api/types';
import { ToastService } from '@shared/ui/toast';
import { CatalogGroups } from './catalog-groups';

const CREATED: ObjectGroup = {
  id: 'grp-1',
  categoryId: null,
  name: 'Центр',
  color: null,
  isVisibleByDefault: true,
};

function button(el: HTMLElement, text: string): HTMLButtonElement {
  const found = [...el.querySelectorAll('button')].find((b) => b.textContent?.trim() === text);
  if (!found) throw new Error(`Кнопка «${text}» не найдена`);
  return found;
}

describe('CatalogGroups', () => {
  let fixture: ComponentFixture<CatalogGroups>;
  let create: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    create = vi.fn().mockResolvedValue(CREATED);
    TestBed.configureTestingModule({
      providers: [
        { provide: GroupsApi, useValue: { list: vi.fn().mockResolvedValue([]), create, update: vi.fn(), remove: vi.fn() } },
        { provide: CategoriesApi, useValue: { list: vi.fn().mockResolvedValue([]), create: vi.fn(), update: vi.fn(), remove: vi.fn() } },
        {
          provide: ToastService,
          useValue: { success: vi.fn(), error: vi.fn(), info: vi.fn(), fromError: vi.fn(), dismiss: vi.fn() },
        },
      ],
    });
    fixture = TestBed.createComponent(CatalogGroups);
    fixture.detectChanges();
    await fixture.whenStable();
  });

  it('создаёт группу с categoryId null при «Без категории»', async () => {
    const root = fixture.nativeElement as HTMLElement;
    button(root, 'Добавить').click();
    fixture.detectChanges();
    await fixture.whenStable();

    const name = root.querySelector('#gr-name') as HTMLInputElement;
    name.value = 'Центр';
    name.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await fixture.whenStable();

    const form = root.querySelector('form') as HTMLFormElement;
    form.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
    await fixture.whenStable();

    expect(create).toHaveBeenCalledTimes(1);
    expect(create).toHaveBeenCalledWith({ categoryId: null, name: 'Центр', color: null, isVisibleByDefault: true });
  });
});
