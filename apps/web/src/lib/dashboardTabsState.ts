/** Shared by the dashboard page and the sidebar: which tab is open, and the messages that keep them in step. */
export const TAB_KEY = "daksh.dashboard.tab";
export const ASK_TAB = "ask";
export const OPEN_TAB_EVENT = "daksh:open-tab";
export const DASHBOARDS_CHANGED_EVENT = "daksh:dashboards-changed";

/** Remember a tab and tell an open dashboard page to switch to it. */
export function openDashboardTab(id: string): void {
  try { localStorage.setItem(TAB_KEY, id); } catch { /* storage unavailable */ }
  window.dispatchEvent(new CustomEvent(OPEN_TAB_EVENT, { detail: id }));
}
/** The list of dashboards changed (added, renamed, deleted): the sidebar's list should reload. */
export function announceDashboardsChanged(): void {
  window.dispatchEvent(new Event(DASHBOARDS_CHANGED_EVENT));
}
