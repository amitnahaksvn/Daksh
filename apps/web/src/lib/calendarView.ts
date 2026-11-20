import type { CalendarEvent } from "./api";

/** "2026-11-20" for a local Date (never UTC, so late-evening dates don't slip a day). */
export function dayKey(d: Date): string {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
}

/** The 6x7 grid of days shown for a month, starting on Sunday, including the leading/trailing days of neighbouring months. */
export function monthGrid(year: number, month: number): Date[] {
  const first = new Date(year, month, 1);
  const start = new Date(year, month, 1 - first.getDay());
  return Array.from({ length: 42 }, (_, i) => new Date(start.getFullYear(), start.getMonth(), start.getDate() + i));
}

/** The date an event starts on, as shown in the owner's calendar (the date part of the ISO text, so it follows their timezone, not the browser's). */
export function eventDay(e: CalendarEvent): string {
  return e.start.slice(0, 10);
}

/** Events grouped by start day, each day sorted all-day first and then by time. */
export function groupByDay(events: CalendarEvent[]): Record<string, CalendarEvent[]> {
  const out: Record<string, CalendarEvent[]> = {};
  for (const e of events) (out[eventDay(e)] ??= []).push(e);
  for (const list of Object.values(out)) list.sort((a, b) => Number(b.allDay) - Number(a.allDay) || a.start.localeCompare(b.start));
  return out;
}

/** "5:30 PM" from the ISO text, or "All day". Reads the clock digits straight from the text so it matches the owner's timezone. */
export function eventTime(e: CalendarEvent): string {
  if (e.allDay) return "All day";
  const h = Number(e.start.slice(11, 13));
  const m = e.start.slice(14, 16);
  return `${h % 12 === 0 ? 12 : h % 12}:${m} ${h < 12 ? "AM" : "PM"}`;
}

/** Event length in minutes, from the two ISO texts (60 when it cannot be read, and for all-day events). */
export function eventMinutes(e: CalendarEvent): number {
  if (e.allDay) return 60;
  const m = Math.round((Date.parse(e.end) - Date.parse(e.start)) / 60000);
  return Number.isFinite(m) && m >= 5 ? m : 60;
}

/** "17:05" for a time input, read from the ISO text so it matches the owner's timezone. */
export function eventClock(e: CalendarEvent): string {
  return e.allDay ? "09:00" : e.start.slice(11, 16);
}
