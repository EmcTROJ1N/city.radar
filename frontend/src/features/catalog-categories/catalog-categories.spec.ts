import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { CategoriesApi } from '@entities/category';
import { ToastService } from '@shared/ui/toast';
import { CatalogCategories } from './catalog-categories';

function button(el: HTMLElement, text: string): HTMLButtonElement {
  const found = [...el.querySelectorAll('button')].find((b) => b.textContent?.trim() === text);
  if (!found) throw new Error(`Кнопка «${text}» не найдена`);
  return found;
}

async function open(fixture: ComponentFixture<CatalogCategories>): Promise<HTMLElement> {
  const root = fixture.nativeElement as HTMLElement;
  button(root, 'Добавить').click();
  fixture.detectChanges();
  // NgModel выставляет значение контрола в микрозадаче.
  await fixture.whenStable();
  return root;
}

async function type(fixture: ComponentFixture<unknown>, input: HTMLInputElement, value: string): Promise<void> {
  input.value = value;
  input.dispatchEvent(new Event('input'));
  fixture.detectChanges();
  await fixture.whenStable();
}

describe('CatalogCategories', () => {
  let fixture: ComponentFixture<CatalogCategories>;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        {
          provide: CategoriesApi,
          useValue: {
            list: vi.fn().mockResolvedValue([]),
            create: vi.fn(),
            update: vi.fn(),
            remove: vi.fn(),
          },
        },
        {
          provide: ToastService,
          useValue: { success: vi.fn(), error: vi.fn(), info: vi.fn(), fromError: vi.fn(), dismiss: vi.fn() },
        },
      ],
    });
    fixture = TestBed.createComponent(CatalogCategories);
    fixture.detectChanges();
    await fixture.whenStable();
  });

  it('подставляет код из названия, пока код не правили вручную', async () => {
    const root = await open(fixture);
    const name = root.querySelector('#cat-name') as HTMLInputElement;
    const code = root.querySelector('#cat-code') as HTMLInputElement;

    await type(fixture, name, 'Воды нет');
    expect(code.value).toBe('vody_net');

    await type(fixture, code, 'custom_code');
    await type(fixture, name, 'Вода есть');
    expect(code.value).toBe('custom_code');
  });

  it('показывает ошибку для кода с недопустимыми символами', async () => {
    const root = await open(fixture);

    await type(fixture, root.querySelector('#cat-code') as HTMLInputElement, 'Вода');

    expect(root.textContent).toContain('Только латинские строчные буквы');
    expect(button(root, 'Сохранить').disabled).toBe(true);
  });
});
