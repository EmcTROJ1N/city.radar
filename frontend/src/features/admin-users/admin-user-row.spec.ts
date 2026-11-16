import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AdminApi } from '@entities/user';
import type { AdminUser } from '@shared/api/types';
import { ConfirmService } from '@shared/ui/confirm';
import { ToastService } from '@shared/ui/toast';
import { AdminUserRow } from './admin-user-row';

const USER: AdminUser = {
  id: 'user-1',
  displayName: 'Герман',
  maxUserId: 100500,
  roles: ['user', 'admin'],
  createdAt: '2026-01-10T09:00:00Z',
  lastLoginAt: '2026-11-14T18:30:00Z',
};

describe('AdminUserRow', () => {
  let setRoles: ReturnType<typeof vi.fn>;
  let ask: ReturnType<typeof vi.fn>;

  function setup(user: AdminUser, isSelf: boolean): ComponentFixture<AdminUserRow> {
    const fixture = TestBed.createComponent(AdminUserRow);
    fixture.componentRef.setInput('user', user);
    fixture.componentRef.setInput('isSelf', isSelf);
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => {
    setRoles = vi.fn().mockResolvedValue(USER);
    ask = vi.fn().mockResolvedValue({ confirmed: true, reason: '' });
    TestBed.configureTestingModule({
      providers: [
        { provide: AdminApi, useValue: { setRoles } },
        { provide: ConfirmService, useValue: { ask } },
        {
          provide: ToastService,
          useValue: { success: vi.fn(), error: vi.fn(), info: vi.fn(), fromError: vi.fn(), dismiss: vi.fn() },
        },
      ],
    });
  });

  it('не даёт снять роль администратора у себя', () => {
    const fixture = setup(USER, true);
    const element = fixture.nativeElement as HTMLElement;
    const boxes = Array.from(element.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'));

    expect(boxes).toHaveLength(3);
    expect(boxes[2]?.checked).toBe(true);
    expect(boxes[2]?.disabled).toBe(true);
    expect(boxes[0]?.disabled).toBe(false);
    expect(element.textContent).toContain('Нельзя снять роль администратора у себя');
  });

  it('даёт менять роли другому пользователю и заменяет строку ответом', async () => {
    const fixture = setup(USER, false);
    const element = fixture.nativeElement as HTMLElement;
    const boxes = Array.from(element.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'));

    expect(boxes[2]?.disabled).toBe(false);
    boxes[2]!.click();
    fixture.detectChanges();

    const save = element.querySelector<HTMLButtonElement>('button.btn--primary');
    expect(save?.textContent).toContain('Сохранить');
    save!.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(ask).toHaveBeenCalledWith(
      expect.objectContaining({ title: 'Снять роль администратора?' }),
    );
    expect(setRoles).toHaveBeenCalledWith('user-1', { roles: ['user'] });
  });
});
