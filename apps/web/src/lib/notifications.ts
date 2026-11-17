/**
 * In-app notifications fed by the integrations SignalR hub (today: WhatsApp message arrivals only).
 * Same module-level singleton + listener-set shape as theme.ts, since this codebase keeps global client
 * state as external stores rather than React Context.
 */
import { api, type ActivityItem } from "@/lib/api";
import { getIntegrationsHub } from "@/lib/signalr";

export type AppNotification = {
  id: string;
  connectionId: string;
  integrationKey: "whatsapp";
  isGroup: boolean;
  tags?: string[];
  priority?: boolean;
  title: string;
  body: string;
  createdAt: string;
  read: boolean;
};

const MAX_NOTIFICATIONS = 30;
let notifications: AppNotification[] = [];
let started = false;
const startedAt = Date.now();
const listeners = new Set<() => void>();
const notify = () => listeners.forEach((listener) => listener());

/** A short two-tone chime, generated rather than loaded so there's no audio file to ship. Browsers may refuse
 *  to play before the page has had any interaction — that's fine, the notification still appears. */
function playChime() {
  try {
    const Ctx = window.AudioContext ?? (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext;
    const ctx = new Ctx();
    [880, 1175].forEach((freq, i) => {
      const osc = ctx.createOscillator();
      const gain = ctx.createGain();
      osc.frequency.value = freq;
      gain.gain.setValueAtTime(0.0001, ctx.currentTime + i * 0.12);
      gain.gain.exponentialRampToValueAtTime(0.15, ctx.currentTime + i * 0.12 + 0.02);
      gain.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + i * 0.12 + 0.25);
      osc.connect(gain).connect(ctx.destination);
      osc.start(ctx.currentTime + i * 0.12);
      osc.stop(ctx.currentTime + i * 0.12 + 0.3);
    });
    setTimeout(() => void ctx.close(), 800);
  } catch {
    /* no audio available */
  }
}

function showDesktop(title: string, body: string) {
  try {
    if (typeof Notification !== "undefined" && Notification.permission === "granted") new Notification(title, { body, tag: title });
  } catch {
    /* some browsers only allow notifications from a service worker */
  }
}

function addNotification(n: AppNotification) {
  notifications = [n, ...notifications].slice(0, MAX_NOTIFICATIONS);
  notify();
}

/** Idempotent: wires the SignalR hub exactly once no matter how many components call it. */
export function startNotifications(): void {
  if (started) return;
  started = true;

  const hub = getIntegrationsHub();
  hub.on(
    "WhatsAppMessageReceived",
    (payload: { connectionId: string; chatName?: string; isGroup?: boolean; preview?: string; timestamp?: string; notify?: boolean; tags?: string[]; priority?: boolean; sound?: boolean; desktop?: boolean }) => {
      // The event also keeps open chat views in sync; the server says whether it's a notification too.
      if (payload.notify === false) return;
      const who = payload.chatName ?? "a contact";
      const title = payload.isGroup ? `WhatsApp group — ${who}` : `WhatsApp — ${who}`;
      addNotification({
        id: `${payload.connectionId}:${payload.timestamp ?? Date.now()}`,
        connectionId: payload.connectionId,
        integrationKey: "whatsapp",
        isGroup: payload.isGroup ?? false,
        tags: payload.tags,
        priority: payload.priority,
        title,
        body: payload.preview ?? "",
        createdAt: payload.timestamp ?? new Date().toISOString(),
        read: false,
      });
      if (payload.sound) playChime();
      if (payload.desktop) showDesktop(title, payload.preview ?? "New message");
    },
  );
  hub.on("WhatsAppReminderDue", (payload: { connectionId: string; chatName?: string; text: string; dueAt?: string }) => {
    addNotification({
      id: `reminder:${payload.connectionId}:${payload.dueAt ?? Date.now()}:${payload.text}`,
      connectionId: payload.connectionId,
      integrationKey: "whatsapp",
      isGroup: false,
      title: payload.chatName ? `Reminder — ${payload.chatName}` : "Reminder",
      body: payload.text,
      createdAt: new Date().toISOString(),
      read: false,
    });
    playChime();
    showDesktop("Reminder", payload.text);
  });
  // WhatsAppMessageSent fires for every outgoing message (it also keeps open chat views in sync); only the
  // ones the server flags via the "Outgoing message notifications" setting become bell notifications.
  hub.on("WhatsAppMessageSent", (payload: { connectionId: string; chatName?: string; isGroup?: boolean; preview?: string; timestamp?: string; notifyOutgoing?: boolean }) => {
    if (!payload.notifyOutgoing) return;
    const who = payload.chatName ?? (payload.isGroup ? "a group" : "a contact");
    addNotification({
      id: `out:${payload.connectionId}:${payload.timestamp ?? Date.now()}`,
      connectionId: payload.connectionId,
      integrationKey: "whatsapp",
      isGroup: payload.isGroup ?? false,
      title: `You sent a message to ${who}`,
      body: payload.preview ?? "",
      createdAt: payload.timestamp ?? new Date().toISOString(),
      read: false,
    });
  });
  if (hub.state === "Disconnected") hub.start().catch(() => void 0);

  // The hub only ever delivers events that happen while this tab is open — without this, the bell shows
  // "Nothing yet" after every fresh page load even when messages arrived earlier. Seed from the same
  // recent-activity feed the dashboard uses, marked already-read since it's history, not a new arrival.
  // mergeFromActivity only adds items not already known, so a live event that arrives first is unaffected.
  void mergeFromActivity({ markNewAsRead: true });

  // SignalR should make the above redundant going forward, but a dropped/stuck connection has repeatedly
  // left the bell stale — poll the same feed as a fallback so it catches up within a few seconds either way.
  setInterval(() => void mergeFromActivity({ markNewAsRead: false }), 8000);
}

function toNotification(item: ActivityItem, read: boolean): AppNotification {
  const who = item.chatName ?? "a contact";
  return {
    id: `${item.connectionId}:${item.chatId}:${item.timestamp}`,
    connectionId: item.connectionId,
    integrationKey: "whatsapp",
    isGroup: item.isGroup,
    title: item.isGroup ? `WhatsApp group — ${who}` : `WhatsApp — ${who}`,
    body: item.text,
    createdAt: item.timestamp,
    read,
  };
}

/** Adds any activity items not already known, without touching the read/unread state of ones already in
 *  the list. `markNewAsRead` is true only for the initial history catch-up (it's backfill, not a fresh
 *  arrival); later polling passes mark genuinely-new items unread so a missed live push still gets noticed. */
async function mergeFromActivity({ markNewAsRead }: { markNewAsRead: boolean }): Promise<void> {
  try {
    // Live pushes are authoritative (they carry the user's notification filters). The poll is only a fallback
    // for when the hub is down, and must never resurface messages from before this page was opened.
    if (!markNewAsRead && getIntegrationsHub().state === "Connected") return;
    const items = await api.dashboardActivity();
    const known = new Set(notifications.map((n) => n.id));
    const knownTimes = new Set(notifications.map((n) => new Date(n.createdAt).getTime()));
    const fresh = items
      .filter((item) => !item.fromMe)
      .filter((item) => markNewAsRead || (new Date(item.timestamp).getTime() > startedAt && !knownTimes.has(new Date(item.timestamp).getTime())))
      .map((item) => toNotification(item, markNewAsRead))
      .filter((n) => !known.has(n.id));
    if (fresh.length === 0) return;
    notifications = [...fresh, ...notifications]
      .sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime())
      .slice(0, MAX_NOTIFICATIONS);
    notify();
  } catch {
    /* best-effort */
  }
}

export function getNotifications(): AppNotification[] {
  return notifications;
}

export function getUnreadCount(): number {
  return notifications.filter((n) => !n.read).length;
}

export function markAllRead(): void {
  if (notifications.every((n) => n.read)) return;
  notifications = notifications.map((n) => ({ ...n, read: true }));
  notify();
}

export function subscribeNotifications(onChange: () => void): () => void {
  listeners.add(onChange);
  return () => listeners.delete(onChange);
}
