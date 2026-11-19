import { describe, expect, it } from "vitest";
import { buildNeedsYou, buildPulse, buildToday, nextUp, relativeTime } from "./commandCentre";
import type { AuditEvent, DashboardApp, ReminderDto, TaskDto } from "./api";

const now = new Date("2026-11-19T12:00:00");
const app = (over: Partial<DashboardApp>): DashboardApp => ({
  id: "a", connectionId: "c", integrationKey: "whatsapp", name: "WhatsApp", icon: "", color: "", status: "Connected", accountIdentifier: null, lastActiveAt: null,
  displayOrder: 0, unreadCount: 0, lastMessagePreview: null, lastMessageAt: null, lastMessageIsGroup: false, cols: 0, height: 0, col: 0, ...over,
});
const task = (over: Partial<TaskDto>): TaskDto => ({ id: "t", title: "Pay invoice", dueAt: null, completed: false, ...over });

describe("buildNeedsYou", () => {
  it("puts overdue tasks first, then drafted replies, broken connections and unread", () => {
    const items = buildNeedsYou({
      now,
      tasks: [task({ id: "late", dueAt: "2026-11-18T09:00:00", title: "Send report" }), task({ id: "future", dueAt: "2026-11-20T09:00:00" }), task({ id: "done", dueAt: "2026-11-01T09:00:00", completed: true })],
      pending: [{ connectionId: "c1", reply: { id: "r1", chatId: "9199@s.whatsapp.net", chatName: "Rohan", text: "Yes, I’ll call at 8.", createdAt: "" } }],
      apps: [app({ id: "tg", integrationKey: "telegram", name: "Telegram", status: "Error" }), app({ id: "wa", unreadCount: 3, lastMessagePreview: "hello" })],
    });
    expect(items.map((i) => i.source)).toEqual(["TASK", "WA", "APP", "UNREAD"]);
    expect(items[0].action).toMatchObject({ kind: "complete-task", taskId: "late" });
    expect(items[1]).toMatchObject({ title: "Reply to Rohan is ready", detail: "Yes, I’ll call at 8." });
    expect(items[1].action).toMatchObject({ kind: "approve-reply", connectionId: "c1", replyId: "r1" });
    expect(items[1].secondary).toMatchObject({ kind: "discard-reply" });
    expect(items[2].action).toMatchObject({ kind: "link", href: "/apps/telegram" });
    expect(items[3].title).toBe("3 unread on WhatsApp");
  });

  it("ignores Gmail unread (it has its own card) and apps that are not connected", () => {
    const items = buildNeedsYou({ now, tasks: [], pending: [], apps: [app({ integrationKey: "gmail", name: "Gmail", unreadCount: 9 }), app({ id: "x", status: "NotConnected", unreadCount: 4 })] });
    expect(items).toEqual([]);
  });
});

describe("timeline", () => {
  const tasks = [task({ id: "1", title: "Today task", dueAt: "2026-11-19T18:00:00" }), task({ id: "2", title: "Tomorrow", dueAt: "2026-11-20T10:00:00" }), task({ id: "3", title: "Past", dueAt: "2026-11-19T08:00:00" })];
  const reminders: ReminderDto[] = [{ id: "r", text: "Call Rohan", remindAt: "2026-11-19T19:50:00" }];
  it("lists only what is still to come today, soonest first", () => {
    expect(buildToday(tasks, reminders, now).map((i) => i.label)).toEqual(["Today task", "Call Rohan"]);
  });
  it("finds the next dated item within a week", () => {
    expect(nextUp(tasks, reminders, now)?.label).toBe("Today task");
    expect(nextUp([], [], now)).toBeNull();
  });
  it("describes time in a friendly way", () => {
    expect(relativeTime(new Date("2026-11-19T12:18:00"), now)).toBe("in 18 min");
    expect(relativeTime(new Date("2026-11-19T15:00:00"), now)).toBe("in 3 h");
    expect(relativeTime(new Date("2026-11-20T12:00:00"), now)).toBe("tomorrow");
    expect(relativeTime(new Date("2026-11-19T11:00:00"), now)).toBe("now");
  });
});

describe("buildPulse", () => {
  it("counts today's activity, open and overdue tasks, unread and healthy connections", () => {
    const audit: AuditEvent[] = [
      { id: "1", action: "a", entityType: null, entityId: null, detail: null, occurredAt: "2026-11-19T08:00:00" },
      { id: "2", action: "b", entityType: null, entityId: null, detail: null, occurredAt: "2026-11-18T08:00:00" },
    ];
    const pulse = buildPulse({ now, audit, tasks: [task({ dueAt: "2026-11-18T09:00:00" }), task({ id: "2" }), task({ id: "3", completed: true })], apps: [app({ unreadCount: 2 }), app({ id: "b", status: "Error", unreadCount: 5 })] });
    expect(pulse).toEqual({ handledToday: 1, openTasks: 2, overdue: 1, unread: 2, healthy: 1, total: 2 });
  });
});
