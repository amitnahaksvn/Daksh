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

/** Chat history: the sidebar asks the Ask tab to open a past chat (or start a fresh one with no id), and the Ask tab says when the list changed. */
export const OPEN_CHAT_EVENT = "daksh:open-chat";
export const CHATS_CHANGED_EVENT = "daksh:chats-changed";
let pendingChat: string | null | undefined;
/** A chat asked for before the Ask tab was on screen (the sidebar was used from another page); the tab collects it when it appears. */
export function takePendingChat(): string | null | undefined { const p = pendingChat; pendingChat = undefined; return p; }
export function openChat(id: string | null): void {
  pendingChat = id;
  openDashboardTab(ASK_TAB);
  window.dispatchEvent(new CustomEvent(OPEN_CHAT_EVENT, { detail: id }));
}
export function announceChatsChanged(): void {
  window.dispatchEvent(new Event(CHATS_CHANGED_EVENT));
}
