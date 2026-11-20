"use client";
import Link from "next/link";
import { useCallback, useEffect, useMemo, useState } from "react";
import { api, ApiError, type CalendarEvent, type CalendarEvents } from "@/lib/api";
import { dayKey, eventClock, eventDay, eventMinutes, eventTime, groupByDay, monthGrid } from "@/lib/calendarView";
import { GmailConnectCard } from "@/components/GmailConnectCard";
import { buttonClass, EmptyState, inputClass, Loading, Notice, PageHeader, tileClass } from "@/components/ui";

const WEEKDAYS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

export default function CalendarPage() {
  const now = new Date();
  const [cursor, setCursor] = useState({ year: now.getFullYear(), month: now.getMonth() });
  const [selected, setSelected] = useState(dayKey(now));
  const [result, setResult] = useState<{ range: string; data?: CalendarEvents; error?: string }>();
  // The form adds an event, or (when `editing` is set) changes that one.
  const [editing, setEditing] = useState<CalendarEvent | null>(null);
  const [title, setTitle] = useState("");
  const [date, setDate] = useState(dayKey(now));
  const [time, setTime] = useState("09:00");
  const [minutes, setMinutes] = useState(60);
  const [location, setLocation] = useState("");
  const [accountId, setAccountId] = useState("");
  const [saving, setSaving] = useState(false);
  const [formError, setFormError] = useState<string>();

  const grid = useMemo(() => monthGrid(cursor.year, cursor.month), [cursor]);
  const range = `${dayKey(grid[0])}..${dayKey(grid[41])}`;

  const [tick, setTick] = useState(0);
  const load = useCallback(() => setTick((t) => t + 1), []);

  useEffect(() => {
    let cancelled = false;
    api.calendarEvents(range.slice(0, 10), range.slice(12)).then(
      (data) => { if (!cancelled) setResult({ range, data }); },
      (err) => { if (!cancelled) setResult({ range, error: err instanceof ApiError ? err.message : "Could not load your calendar. Please try again." }); },
    );
    return () => { cancelled = true; };
  }, [range, tick]);

  // Loading is simply "what we have is for a different month than the one on screen".
  const loading = result?.range !== range;
  const data = result?.data;
  const loadError = result?.error;

  const byDay = useMemo(() => groupByDay(data?.events ?? []), [data]);
  const today = data?.today ?? dayKey(now);
  const dayEvents = byDay[selected] ?? [];
  const accounts = data?.accounts ?? [];
  const usable = accounts.filter((a) => a.hasCalendar);
  const writeTo = usable.find((a) => a.id === accountId) ?? usable[0];
  const needAccess = accounts.filter((a) => !a.hasCalendar || a.error);
  const monthLabel = new Date(cursor.year, cursor.month, 1).toLocaleDateString(undefined, { month: "long", year: "numeric" });

  function shift(delta: number) {
    const d = new Date(cursor.year, cursor.month + delta, 1);
    setCursor({ year: d.getFullYear(), month: d.getMonth() });
  }

  function pickDay(key: string) {
    setSelected(key);
    if (!editing) setDate(key);
  }

  function startEdit(e: CalendarEvent) {
    setEditing(e);
    setFormError(undefined);
    setTitle(e.title);
    setDate(eventDay(e));
    setTime(eventClock(e));
    setMinutes(eventMinutes(e));
    setLocation(e.location ?? "");
  }

  function stopEdit() {
    setEditing(null);
    setFormError(undefined);
    setTitle(""); setLocation(""); setDate(selected); setTime("09:00"); setMinutes(60);
  }

  async function save(e: React.FormEvent) {
    e.preventDefault();
    setFormError(undefined);
    if (!title.trim()) return;
    setSaving(true);
    try {
      if (editing) {
        // An all-day event keeps its dates (only the title and place change here); a timed one can move.
        await api.updateCalendarEvent(editing.id, editing.accountId, editing.allDay
          ? { title: title.trim(), location: location.trim() }
          : { title: title.trim(), start: `${date}T${time}:00`, minutes, location: location.trim() });
        stopEdit();
      } else {
        await api.addCalendarEvent(title.trim(), `${date}T${time}:00`, minutes, location.trim(), writeTo?.id);
        setTitle(""); setLocation("");
      }
      load();
    } catch (err) {
      setFormError(err instanceof ApiError ? err.message : "Could not save the event. Please try again.");
    } finally {
      setSaving(false);
    }
  }

  async function remove(ev: CalendarEvent) {
    if (!window.confirm(`Delete "${ev.title}" from your Google Calendar?`)) return;
    try {
      await api.deleteCalendarEvent(ev.id, ev.accountId);
      if (editing?.id === ev.id) stopEdit();
      load();
    } catch (err) { setFormError(err instanceof ApiError ? err.message : "Could not delete the event."); }
  }

  const timedFields = !editing || !editing.allDay;

  return (
    <>
      <PageHeader title="Calendar">
        Your Google Calendar, from all your connected Google accounts. Add, edit or delete events here, or message yourself: &ldquo;dentist tomorrow 5pm&rdquo;, &ldquo;what&apos;s on my calendar today&rdquo;.
      </PageHeader>

      {data && !data.connected && (
        <Notice>
          Google Calendar isn&apos;t connected yet. Connect a Google account below (or reconnect it from <Link href="/apps/gmail" className="font-semibold underline underline-offset-4">Gmail</Link>) and tick the Calendar permission. One Google sign-in covers both.
        </Notice>
      )}
      {data?.connected && needAccess.length > 0 && (
        <Notice>
          Not shown: {needAccess.map((a) => a.email).join(", ")}. {needAccess.some((a) => a.error) ? "Google could not read it just now, or it needs reconnecting." : "It was connected without Calendar."}{" "}
          Connect it again below and tick Calendar.
        </Notice>
      )}
      {data?.status === "Failed" && <p role="alert" className="mb-4 text-sm text-danger">{data.message ?? "Google Calendar could not be read right now."}</p>}
      {loadError && <p role="alert" className="mb-4 text-sm text-danger">{loadError}</p>}

      <div className="grid gap-4 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
        <section className={`p-5 ${tileClass}`} aria-label="Month">
          <div className="mb-3 flex items-center justify-between gap-2">
            <button type="button" className={buttonClass} onClick={() => shift(-1)} aria-label="Previous month">‹</button>
            <h2 className="text-lg font-bold">{monthLabel}</h2>
            <button type="button" className={buttonClass} onClick={() => shift(1)} aria-label="Next month">›</button>
          </div>
          <div className="grid grid-cols-7 gap-1 text-center text-xs text-muted">
            {WEEKDAYS.map((d) => <div key={d} className="py-1">{d}</div>)}
          </div>
          <div className="grid grid-cols-7 gap-1">
            {grid.map((d) => {
              const key = dayKey(d);
              const count = byDay[key]?.length ?? 0;
              const inMonth = d.getMonth() === cursor.month;
              return (
                <button
                  key={key}
                  type="button"
                  onClick={() => pickDay(key)}
                  aria-label={`${d.toLocaleDateString(undefined, { weekday: "long", day: "numeric", month: "long" })}${count ? `, ${count} event${count === 1 ? "" : "s"}` : ""}`}
                  aria-pressed={key === selected}
                  className={`flex h-14 flex-col items-center justify-center rounded-xl text-sm transition-colors ${key === selected ? "bg-pink text-on-tint font-bold" : "hover:bg-pink/40"} ${inMonth ? "" : "text-muted opacity-60"} ${key === today ? "ring-2 ring-accent" : ""}`}
                >
                  <span>{d.getDate()}</span>
                  {count > 0 && <span className="mt-0.5 text-[10px] leading-none">{"•".repeat(Math.min(count, 3))}</span>}
                </button>
              );
            })}
          </div>
          {loading && <div className="mt-3"><Loading label="Loading events" /></div>}
        </section>

        <div className="flex flex-col gap-4">
          <section className={`p-5 ${tileClass}`} aria-label="Day">
            <h2 className="mb-3 text-lg font-bold">{new Date(`${selected}T00:00:00`).toLocaleDateString(undefined, { weekday: "long", day: "numeric", month: "long" })}</h2>
            {!loading && dayEvents.length === 0 && <EmptyState>Nothing planned.</EmptyState>}
            <ul className="mb-4 flex flex-col gap-2">
              {dayEvents.map((e) => (
                <li key={`${e.accountId}:${e.id}`} className={`flex items-start gap-3 rounded-xl p-3 ${editing?.id === e.id ? "bg-pink ring-2 ring-accent" : "bg-pink/30"}`}>
                  <span className="w-20 shrink-0 text-sm font-semibold">{eventTime(e)}</span>
                  <span className="min-w-0 flex-1 break-words">
                    {e.htmlLink ? <a href={e.htmlLink} target="_blank" rel="noreferrer" className="underline-offset-4 hover:underline">{e.title}</a> : e.title}
                    {e.location && <span className="block text-sm text-muted">{e.location}</span>}
                    {usable.length > 1 && e.account && <span className="block text-xs text-muted">{e.account}</span>}
                  </span>
                  <button type="button" aria-label={`Edit ${e.title}`} className="text-sm text-muted hover:text-text" onClick={() => startEdit(e)}>✎</button>
                  <button type="button" aria-label={`Delete ${e.title}`} className="text-sm text-muted hover:text-danger" onClick={() => void remove(e)}>✕</button>
                </li>
              ))}
            </ul>

            {(data?.connected !== false) && (
              <form onSubmit={save} className="flex flex-col gap-2" aria-label={editing ? "Edit event" : "Add event"}>
                {editing && <p className="text-sm font-semibold">Editing &ldquo;{editing.title}&rdquo;{usable.length > 1 && editing.account ? ` (${editing.account})` : ""}</p>}
                <label className="sr-only" htmlFor="ev-title">Event title</label>
                <input id="ev-title" className={inputClass} value={title} onChange={(e) => setTitle(e.target.value)} placeholder="Event title" maxLength={200} />
                {timedFields && (
                  <div className="flex flex-wrap gap-2">
                    <label className="sr-only" htmlFor="ev-date">Date</label>
                    <input id="ev-date" type="date" className={`${inputClass} min-w-0 flex-1`} value={date} onChange={(e) => setDate(e.target.value)} />
                    <label className="sr-only" htmlFor="ev-time">Start time</label>
                    <input id="ev-time" type="time" className={`${inputClass} w-32`} value={time} onChange={(e) => setTime(e.target.value)} />
                    <label className="sr-only" htmlFor="ev-len">Length</label>
                    <select id="ev-len" className={`${inputClass} min-w-0 flex-1`} value={minutes} onChange={(e) => setMinutes(Number(e.target.value))}>
                      {[15, 30, 45, 60, 90, 120, 240].map((m) => <option key={m} value={m}>{m < 60 ? `${m} min` : m === 60 ? "1 hour" : `${m / 60} hours`}</option>)}
                      {![15, 30, 45, 60, 90, 120, 240].includes(minutes) && <option value={minutes}>{minutes} min</option>}
                    </select>
                  </div>
                )}
                {!editing && usable.length > 1 && (
                  <>
                    <label className="sr-only" htmlFor="ev-acct">Add to account</label>
                    <select id="ev-acct" className={inputClass} value={writeTo?.id ?? ""} onChange={(e) => setAccountId(e.target.value)}>
                      {usable.map((a) => <option key={a.id} value={a.id}>{a.email}</option>)}
                    </select>
                  </>
                )}
                <label className="sr-only" htmlFor="ev-loc">Location</label>
                <input id="ev-loc" className={inputClass} value={location} onChange={(e) => setLocation(e.target.value)} placeholder="Location (optional)" maxLength={200} />
                {formError && <p role="alert" className="text-sm text-danger">{formError}</p>}
                <div className="flex gap-2">
                  <button className={`${buttonClass} flex-1`} disabled={saving || !title.trim() || (timedFields && !date)}>{saving ? "Saving…" : editing ? "Save changes" : "Add to calendar"}</button>
                  {editing && <button type="button" onClick={stopEdit} className="rounded-full border border-line px-4 py-2 text-sm font-semibold hover:bg-surface">Cancel</button>}
                </div>
              </form>
            )}
          </section>

          <GmailConnectCard
            onConnected={load}
            title={usable.length === 0 ? "Connect a Google account" : "Connect another Google account"}
            description="Google will let you choose the account. Tick Calendar (and Gmail) when it asks: one sign-in covers both. Events from every connected account appear here."
            buttonLabel="Connect with Google"
          />
        </div>
      </div>
    </>
  );
}
