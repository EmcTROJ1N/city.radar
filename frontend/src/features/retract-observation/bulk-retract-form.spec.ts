import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ObservationsApi } from '@entities/observation';
import { SourcesApi } from '@entities/source';
import { ConfirmService } from '@shared/ui/confirm';
import { ToastService } from '@shared/ui/toast';
import { BulkRetractForm } from './bulk-retract-form';

describe('BulkRetractForm', () => {
  let retractBulk: ReturnType<typeof vi.fn>;
  let ask: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    retractBulk = vi.fn().mockResolvedValue({ retractedCount: 3 });
    ask = vi.fn().mockResolvedValue({ confirmed: true, reason: '' });
    TestBed.configureTestingModule({
      providers: [
        {
          provide: ObservationsApi,
          useValue: { create: vi.fn(), retract: vi.fn(), retractBulk },
        },
        {
          provide: SourcesApi,
          useValue: { list: vi.fn().mockResolvedValue({ items: [], nextCursor: null }), get: vi.fn() },
        },
        { provide: ConfirmService, useValue: { ask } },
        {
          provide: ToastService,
          useValue: { success: vi.fn(), error: vi.fn(), info: vi.fn(), fromError: vi.fn(), dismiss: vi.fn() },
        },
      ],
    });
  });

  it('не отправляет без автора, источника и периода', async () => {
    const fixture = TestBed.createComponent(BulkRetractForm);
    fixture.detectChanges();

    const reason = fixture.nativeElement.querySelector('#br-reason') as HTMLTextAreaElement;
    reason.value = 'Ошибочные отметки из тестового чата';
    reason.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    const form = fixture.nativeElement.querySelector('form') as HTMLFormElement;
    form.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
    await fixture.whenStable();

    expect(ask).not.toHaveBeenCalled();
    expect(retractBulk).not.toHaveBeenCalled();
  });
});
