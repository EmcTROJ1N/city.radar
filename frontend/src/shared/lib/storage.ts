// localStorage может быть недоступен (приватный режим) — не падаем, просто не запоминаем.
export const storage = {
  get(key: string): string | null {
    try { return localStorage.getItem(key); } catch { return null; }
  },
  set(key: string, value: string): void {
    try { localStorage.setItem(key, value); } catch { /* не запоминаем */ }
  },
};
