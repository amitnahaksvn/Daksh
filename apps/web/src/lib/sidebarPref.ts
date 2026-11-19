/** Whether the sidebar is minimized to a slim strip. A per-browser convenience, so it lives in localStorage and never blocks the page. */
export const SIDEBAR_KEY = "daksh.sidebar.collapsed";
const listeners = new Set<() => void>();

export function readSidebarCollapsed(): boolean {
  try { return window.localStorage.getItem(SIDEBAR_KEY) === "1"; } catch { return false; }
}
export function setSidebarCollapsed(collapsed: boolean): void {
  try { window.localStorage.setItem(SIDEBAR_KEY, collapsed ? "1" : "0"); } catch { /* storage unavailable: applies for this visit only */ }
  memory = collapsed;
  listeners.forEach((l) => l());
}
let memory: boolean | null = null;
/** The current value, falling back to what was set this visit when storage is unavailable. */
export const sidebarSnapshot = (): boolean => (memory !== null && !storageWorks() ? memory : readSidebarCollapsed());
function storageWorks(): boolean { try { window.localStorage.getItem(SIDEBAR_KEY); return true; } catch { return false; } }

export function subscribeSidebar(onChange: () => void): () => void {
  listeners.add(onChange);
  const onStorage = (e: StorageEvent) => { if (e.key === SIDEBAR_KEY) onChange(); };
  window.addEventListener("storage", onStorage);
  return () => { listeners.delete(onChange); window.removeEventListener("storage", onStorage); };
}
