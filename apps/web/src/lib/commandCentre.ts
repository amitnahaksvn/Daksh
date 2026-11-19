/**
 * Pure logic for the dashboard's Command Centre: merges every integration into one "Needs you" list, finds what is next
 * and summarises the day. No React and no network, so it is tested directly.
 */
import type { AuditEvent, DashboardApp, ReminderDto, TaskDto, WhatsAppPendingReply } from "@/lib/api";

export type NeedsSource = "WA" | "TASK" | "APP" | "UNREAD";
export type NeedsAction =
  | { kind: "approve-reply"; label: string; connectionId: string; replyId: string }
  | { kind: "complete-task"; label: string; taskId: string }
  | { kind: "link"; label: string; href: string };
export type NeedsItem = {
  id: string;
  source: NeedsSource;
  title: string;
  detail: string;
  action: NeedsAction;
  /** A second, quieter action (for example discarding a drafted reply). */
  secondary?: { kind: "discard-reply"; label: string; connectionId: string; replyId: string };
};
export type PendingWithConnection = { connectionId: string; reply: WhatsAppPendingReply };

const MESSAGING_PATH: Record<string, string> = { whatsapp: "/apps/whatsapp", telegram: "/apps/telegram", gmail: "/apps/gmail" };
export const appHref = (key: string) => MESSAGING_PATH[key] ?? "/apps";
const isProblem = (status: DashboardApp["status"]) => status === "Error" || status === "Disconnected";
const startOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();

/** Everything that needs the user, most urgent first: overdue tasks, drafted replies, broken connections, unread messages. */
export function buildNeedsYou(input: { pending: PendingWithConnection[]; tasks: TaskDto[]; apps: DashboardApp[]; now?: Date }): NeedsItem[] {
  const now = (input.now ?? new Date()).getTime();
  const items: NeedsItem[] = [];

  for (const t of input.tasks) {
    if (t.completed || !t.dueAt || new Date(t.dueAt).getTime() >= now) continue;
    items.push({ id: `task-${t.id}`, source: "TASK", title: t.title, detail: `Overdue since ${new Date(t.dueAt).toLocaleDateString(undefined, { day: "numeric", month: "short" })}`, action: { kind: "complete-task", label: "Mark done", taskId: t.id } });
  }
  for (const { connectionId, reply } of input.pending) {
    const who = reply.chatName ?? reply.chatId.split("@")[0];
    items.push({
      id: `reply-${reply.id}`, source: "WA", title: `Reply to ${who} is ready`, detail: reply.text,
      action: { kind: "approve-reply", label: "Send", connectionId, replyId: reply.id },
      secondary: { kind: "discard-reply", label: "Discard", connectionId, replyId: reply.id },
    });
  }
  for (const a of input.apps) {
    if (isProblem(a.status)) items.push({ id: `app-${a.id}`, source: "APP", title: `${a.name} needs attention`, detail: a.status === "Error" ? "The connection reported an error." : "The connection was lost.", action: { kind: "link", label: "Fix it", href: appHref(a.integrationKey) } });
  }
  for (const a of input.apps) {
    if (a.status === "Connected" && a.unreadCount > 0 && a.integrationKey !== "gmail") {
      items.push({ id: `unread-${a.id}`, source: "UNREAD", title: `${a.unreadCount} unread on ${a.name}`, detail: a.lastMessagePreview ?? "Open the chat to read.", action: { kind: "link", label: "Open", href: appHref(a.integrationKey) } });
    }
  }
  return items;
}

export type TimelineItem = { id: string; at: Date; label: string; kind: "task" | "reminder" };

/** Tasks that are due and reminders that fire from now to the end of today, soonest first. */
export function buildToday(tasks: TaskDto[], reminders: ReminderDto[], now = new Date()): TimelineItem[] {
  const endOfDay = startOfDay(now) + 24 * 3600 * 1000;
  const from = now.getTime();
  const out: TimelineItem[] = [];
  for (const t of tasks) if (!t.completed && t.dueAt) { const at = new Date(t.dueAt); if (at.getTime() >= from && at.getTime() < endOfDay) out.push({ id: `t-${t.id}`, at, label: t.title, kind: "task" }); }
  for (const r of reminders) { const at = new Date(r.remindAt); if (at.getTime() >= from && at.getTime() < endOfDay) out.push({ id: `r-${r.id}`, at, label: r.text, kind: "reminder" }); }
  return out.sort((a, b) => a.at.getTime() - b.at.getTime());
}

/** The next dated thing in the next 7 days (task or reminder), or null. */
export function nextUp(tasks: TaskDto[], reminders: ReminderDto[], now = new Date()): TimelineItem | null {
  const from = now.getTime(); const to = from + 7 * 24 * 3600 * 1000;
  const all: TimelineItem[] = [];
  for (const t of tasks) if (!t.completed && t.dueAt) { const at = new Date(t.dueAt); if (at.getTime() >= from && at.getTime() <= to) all.push({ id: `t-${t.id}`, at, label: t.title, kind: "task" }); }
  for (const r of reminders) { const at = new Date(r.remindAt); if (at.getTime() >= from && at.getTime() <= to) all.push({ id: `r-${r.id}`, at, label: r.text, kind: "reminder" }); }
  return all.sort((a, b) => a.at.getTime() - b.at.getTime())[0] ?? null;
}

/** "in 18 min", "in 3 h", "tomorrow" — short and friendly. */
export function relativeTime(at: Date, now = new Date()): string {
  const mins = Math.round((at.getTime() - now.getTime()) / 60000);
  if (mins <= 0) return "now";
  if (mins < 60) return `in ${mins} min`;
  if (mins < 24 * 60) { const h = Math.round(mins / 60); return `in ${h} h`; }
  const days = Math.round(mins / (24 * 60));
  return days === 1 ? "tomorrow" : `in ${days} days`;
}

export type Pulse = { handledToday: number; openTasks: number; overdue: number; unread: number; healthy: number; total: number };
export function buildPulse(input: { audit: AuditEvent[]; tasks: TaskDto[]; apps: DashboardApp[]; now?: Date }): Pulse {
  const now = input.now ?? new Date();
  const day = startOfDay(now);
  const open = input.tasks.filter((t) => !t.completed);
  return {
    handledToday: input.audit.filter((e) => new Date(e.occurredAt).getTime() >= day).length,
    openTasks: open.length,
    overdue: open.filter((t) => t.dueAt && new Date(t.dueAt).getTime() < now.getTime()).length,
    unread: input.apps.reduce((n, a) => n + (a.status === "Connected" ? a.unreadCount : 0), 0),
    healthy: input.apps.filter((a) => a.status === "Connected").length,
    total: input.apps.length,
  };
}

export type DashboardView = "overview" | "focus" | "ops";
export const VIEW_KEY = "daksh.dashboardView";
export const isView = (v: string | null): v is DashboardView => v === "overview" || v === "focus" || v === "ops";
