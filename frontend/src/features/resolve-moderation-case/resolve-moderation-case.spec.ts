import { provideRouter } from '@angular/router';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { CatalogObjectsApi } from '@entities/map-object';
import { ModerationApi } from '@entities/moderation-case';
import { SourcesApi } from '@entities/source';
import type { ModerationCase } from '@shared/api/types';
import { ConfirmService } from '@shared/ui/confirm';
import { ToastService } from '@shared/ui/toast';
import { ResolveModerationCase } from './resolve-moderation-case';

const CASE: ModerationCase = {
  id: 'case-1',
  kind: 'object_not_found',
  state: 'open',
  createdAt: '2026-11-15T09:00:00Z',
  sourceName: 'Чат жильцов',
  mapObjectName: null,
  messageExcerpt: 'на втором пункте тоже привезли бочки',
  sourceId: 'src-1',
  mapObjectId: null,
  observationId: null,
  rawMessage: null,
  proposed: { statusCode: 'queue', confidence: 0.4, evidenceQuote: null },
  complaintText: null,
  resolvedAt: null,
  resolvedBy: null,
};

describe('ResolveModerationCase', () => {
  let approve: ReturnType<typeof vi.fn>;
  let reject: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    approve = vi.fn().mockResolvedValue({ ...CASE, state: 'approved' });
    reject = vi.fn().mockResolvedValue({ ...CASE, state: 'rejected' });
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: ModerationApi, useValue: { approve, reject, list: vi.fn(), get: vi.fn() } },
        {
          provide: SourcesApi,
          useValue: {
            get: vi.fn().mockResolvedValue({ id: 'src-1', statuses: [] }),
            list: vi.fn().mockResolvedValue({ items: [], nextCursor: null }),
          },
        },
        {
          provide: CatalogObjectsApi,
          useValue: { list: vi.fn().mockResolvedValue({ items: [], nextCursor: null }) },
        },
        { provide: ConfirmService, useValue: { ask: vi.fn() } },
        {
          provide: ToastService,
          useValue: { success: vi.fn(), error: vi.fn(), info: vi.fn(), fromError: vi.fn(), dismiss: vi.fn() },
        },
      ],
    });
  });

  it('кейс object_not_found без выбранной фиксации принять нельзя', async () => {
    const fixture = TestBed.createComponent(ResolveModerationCase);
    fixture.componentRef.setInput('item', CASE);
    fixture.detectChanges();
    await fixture.whenStable();

    const button = fixture.nativeElement.querySelector('button.approve') as HTMLButtonElement;
    expect(button.disabled).toBe(true);

    button.click();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(approve).not.toHaveBeenCalled();
    expect(reject).not.toHaveBeenCalled();
  });
});
