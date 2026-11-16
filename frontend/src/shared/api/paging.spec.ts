import { describe, expect, it, vi } from 'vitest';
import { collectAll } from './paging';

describe('collectAll', () => {
  it('проходит страницы до nextCursor=null', async () => {
    const load = vi
      .fn()
      .mockResolvedValueOnce({ items: [1, 2], nextCursor: 'c1' })
      .mockResolvedValueOnce({ items: [3], nextCursor: null });

    await expect(collectAll(load)).resolves.toEqual([1, 2, 3]);
    expect(load).toHaveBeenNthCalledWith(1, null);
    expect(load).toHaveBeenNthCalledWith(2, 'c1');
    expect(load).toHaveBeenCalledTimes(2);
  });

  it('останавливается на max', async () => {
    const load = vi
      .fn()
      .mockResolvedValueOnce({ items: [1, 2], nextCursor: 'c1' })
      .mockResolvedValueOnce({ items: [3, 4], nextCursor: 'c2' });

    await expect(collectAll(load, 2)).resolves.toEqual([1, 2]);
    expect(load).toHaveBeenCalledTimes(1);
  });

  it('не зацикливается на повторном курсоре', async () => {
    const load = vi.fn().mockResolvedValue({ items: [1], nextCursor: 'same' });

    await expect(collectAll(load, 10)).resolves.toEqual([1, 1]);
  });
});
