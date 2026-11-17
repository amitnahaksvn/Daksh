import { useSyncExternalStore } from "react";

// Which connected accounts appear in the Chats side panel. Everything shows by default; this only remembers the
// ones the user turned off. Kept per browser (it's a display preference, not account data).
const KEY = "daksh.chats.hidden";
const listeners = new Set<() => void>();

function read(): string[] {
  try {
    const raw = window.localStorage.getItem(KEY);
    const parsed = raw ? (JSON.parse(raw) as unknown) : [];
    return Array.isArray(parsed) ? parsed.filter((x): x is string => typeof x === "string") : [];
  } catch {
    return [];
  }
}

let snapshot = "[]";
function refreshSnapshot() {
  snapshot = JSON.stringify(read());
}

export function setChatShown(connectionId: string, shown: boolean) {
  const hidden = new Set(read());
  if (shown) hidden.delete(connectionId);
  else hidden.add(connectionId);
  try { window.localStorage.setItem(KEY, JSON.stringify([...hidden])); } catch { /* storage unavailable */ }
  refreshSnapshot();
  listeners.forEach((l) => l());
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  refreshSnapshot();
  window.addEventListener("storage", listener);
  return () => {
    listeners.delete(listener);
    window.removeEventListener("storage", listener);
  };
}

function getSnapshot() {
  return snapshot;
}

/** The ids hidden from the Chats panel. */
export function useHiddenChatConnections(): Set<string> {
  const raw = useSyncExternalStore(subscribe, getSnapshot, () => "[]");
  return new Set(JSON.parse(raw) as string[]);
}
