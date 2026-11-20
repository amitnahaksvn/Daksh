import { describe, expect, it } from "vitest";
import type { CalendarEvent } from "./api";
import { dayKey, eventClock, eventMinutes, eventTime, groupByDay, monthGrid } from "./calendarView";

const ev = (id: string, start: string, allDay = false): CalendarEvent => ({ id, title: id, start, end: start, allDay });

describe("calendar view helpers", () => {
  it("builds a 42-day grid that starts on a Sunday and contains the whole month", () => {
    const grid = monthGrid(2026, 10); // November 2026
    expect(grid).toHaveLength(42);
    expect(grid[0].getDay()).toBe(0);
    expect(grid.map(dayKey)).toContain("2026-11-01");
    expect(grid.map(dayKey)).toContain("2026-11-30");
  });

  it("groups events by day with all-day first, then by time", () => {
    const g = groupByDay([ev("late", "2026-11-20T17:00:00+05:30"), ev("holiday", "2026-11-20", true), ev("early", "2026-11-20T09:00:00+05:30"), ev("other", "2026-11-21T10:00:00+05:30")]);
    expect(g["2026-11-20"].map((e) => e.id)).toEqual(["holiday", "early", "late"]);
    expect(g["2026-11-21"]).toHaveLength(1);
  });

  it("works out length and clock for editing an event", () => {
    const e = { ...ev("a", "2026-11-20T17:05:00+05:30"), end: "2026-11-20T18:35:00+05:30" };
    expect(eventMinutes(e)).toBe(90);
    expect(eventClock(e)).toBe("17:05");
    expect(eventMinutes(ev("b", "2026-11-20", true))).toBe(60);
  });

  it("shows the clock time in the owner's timezone", () => {
    expect(eventTime(ev("a", "2026-11-20T17:05:00+05:30"))).toBe("5:05 PM");
    expect(eventTime(ev("b", "2026-11-20T00:00:00+05:30"))).toBe("12:00 AM");
    expect(eventTime(ev("c", "2026-11-20", true))).toBe("All day");
  });
});
