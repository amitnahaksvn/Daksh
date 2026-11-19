"use client";
import { useCallback, useEffect, useRef, useState } from "react";
import { api, ApiError, type BusyProfile } from "@/lib/api";
import { buttonClass, inputClass, tileClass } from "@/components/ui";

const DAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
const QUICK = [{ label: "1 hour", hours: 1 }, { label: "3 hours", hours: 3 }, { label: "8 hours", hours: 8 }];
export const BUSY_CHANGED_EVENT = "daksh:busy-changed";

function systemTimezone(): string {
  try { return Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC"; } catch { return "UTC"; }
}
function timezones(current: string): string[] {
  let zones: string[] = [];
  try { zones = (Intl as unknown as { supportedValuesOf?: (k: string) => string[] }).supportedValuesOf?.("timeZone") ?? []; } catch { /* older runtime */ }
  return [...new Set(["UTC", ...zones, current, systemTimezone()].filter(Boolean))].sort();
}
function untilText(iso: string | null) {
  if (!iso) return "";
  return new Date(iso).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
}

/** Loads and updates the profile's busy settings; shared by the header chip and the profile section. */
function useBusy() {
  const [busy, setBusy] = useState<BusyProfile | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(() => { api.busy().then(setBusy).catch(() => { /* the chip just stays hidden */ }); }, []);
  useEffect(() => {
    load();
    window.addEventListener(BUSY_CHANGED_EVENT, load);
    return () => window.removeEventListener(BUSY_CHANGED_EVENT, load);
  }, [load]);
  async function save(req: Parameters<typeof api.updateBusy>[0]) {
    setError(null);
    try {
      const next = await api.updateBusy(req);
      setBusy(next);
      window.dispatchEvent(new Event(BUSY_CHANGED_EVENT));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not save. Please try again.");
    }
  }
  return { busy, error, save };
}

function untilIn(hours: number) { return new Date(Date.now() + hours * 3_600_000).toISOString(); }
/** The next time the clock reads "HH:mm" (today if still ahead, otherwise tomorrow), as an ISO string. */
function nextClockTime(hhmm: string) {
  const [h, m] = hhmm.split(":").map(Number);
  const d = new Date();
  d.setHours(h, m, 0, 0);
  if (d.getTime() <= Date.now()) d.setDate(d.getDate() + 1);
  return d.toISOString();
}

/** A small "I'm busy" button for the top bar: one click to say how long, one click to say you're free. */
export function BusyChip() {
  const { busy, save } = useBusy();
  const [open, setOpen] = useState(false);
  const [customTime, setCustomTime] = useState("");
  const [note, setNote] = useState("");
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false); };
    document.addEventListener("mousedown", onDown);
    return () => document.removeEventListener("mousedown", onDown);
  }, [open]);
  if (!busy) return null;

  return (
    <div className="relative" ref={ref}>
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        className={`flex items-center gap-2 rounded-full px-3 py-1.5 text-sm font-semibold ${busy.isBusyNow ? "bg-accent text-accent-contrast" : "bg-surface-2 text-muted hover:text-text"}`}
      >
        <span aria-hidden className={`h-2 w-2 rounded-full ${busy.isBusyNow ? "bg-white" : "bg-muted"}`} />
        {busy.isBusyNow ? "Busy" : "I\u2019m busy"}
      </button>
      {open && (
        <div role="menu" className={`absolute right-0 z-40 mt-2 w-60 p-3 ${tileClass}`}>
          <p className="mb-2 text-xs text-muted">
            {busy.isBusyNow ? `Replying for you${busy.until && new Date(busy.until) > new Date() ? ` until ${untilText(busy.until)}` : ""}.` : "Auto-reply on all your apps while you\u2019re busy."}
          </p>
          <input
            value={note}
            onChange={(e) => setNote(e.target.value)}
            maxLength={120}
            placeholder="Reason (optional), e.g. in a meeting"
            aria-label="Busy reason (optional)"
            className={`${inputClass} mb-2 w-full py-1 text-xs`}
          />
          <div className="flex flex-wrap gap-1">
            {QUICK.map((q) => (
              <button key={q.hours} type="button" role="menuitem" onClick={() => { void save({ until: untilIn(q.hours), note }); setOpen(false); }} className="rounded-full bg-surface-2 px-3 py-1 text-xs font-semibold hover:text-text">{q.label}</button>
            ))}
            {busy.isBusyNow && (
              <button type="button" role="menuitem" onClick={() => { void save({ enabled: false, clearUntil: true }); setOpen(false); }} className="rounded-full bg-accent px-3 py-1 text-xs font-semibold text-accent-contrast">I&apos;m free</button>
            )}
          </div>
          <form
            className="mt-2 flex items-center gap-2"
            onSubmit={(e) => { e.preventDefault(); if (customTime) { void save({ until: nextClockTime(customTime), note }); setOpen(false); } }}
          >
            <label className="flex flex-1 items-center gap-2 text-xs text-muted">
              Until
              <input type="time" required value={customTime} onChange={(e) => setCustomTime(e.target.value)} className={`${inputClass} w-full py-1 text-xs`} aria-label="Busy until (time)" />
            </label>
            <button type="submit" className="rounded-full bg-accent px-3 py-1 text-xs font-semibold text-accent-contrast">Set</button>
          </form>
          <a href="/profile" className="mt-2 block text-xs text-accent underline">Busy settings</a>
        </div>
      )}
    </div>
  );
}

/** The full busy-mode settings, shown on the Profile page. */
export function BusyModeForm() {
  const { busy, error, save } = useBusy();
  const [message, setMessage] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const [noteDraft, setNoteText] = useState<string | null>(null);
  if (!busy) return <div className={`p-6 ${tileClass}`}>Loading…</div>;
  const tz = busy.timezone || systemTimezone();
  const note = noteDraft ?? busy.note ?? "";
  const noteText = noteDraft;
  const text = message ?? busy.fixedMessage ?? ""; // what you've typed, else what is saved

  return (
    <div className={`flex flex-col gap-5 p-6 ${tileClass}`}>
      <div>
        <h2 className="text-lg font-bold">Busy mode</h2>
        <p className="mt-1 text-sm text-muted">
          While you&apos;re busy, Daksha replies for you on every connected app (WhatsApp, Telegram…), even if that app&apos;s own auto-reply is off. It never shares your data or which apps you use.
        </p>
      </div>
      {error && <p role="alert" className="rounded-[1.25rem] bg-pink p-3 text-sm text-on-tint">{error}</p>}

      <label className="flex items-center justify-between gap-4 text-sm font-medium">
        Busy mode
        <input type="checkbox" className="h-5 w-5" checked={busy.enabled} onChange={(e) => void save({ enabled: e.target.checked })} />
      </label>

      {busy.enabled && (
        <>
          <div className="text-sm">
            <span className="mb-1 block text-muted">{busy.isBusyNow ? "You\u2019re busy right now." : "You\u2019re not busy right now."} Start a busy period:</span>
            <div className="flex flex-wrap gap-1">
              {QUICK.map((q) => (
                <button key={q.hours} type="button" onClick={() => void save({ until: untilIn(q.hours) })} className="rounded-full bg-surface-2 px-3 py-1.5 text-xs font-semibold hover:text-text">{q.label}</button>
              ))}
              <button type="button" onClick={() => void save({ clearUntil: true })} className="rounded-full bg-surface-2 px-3 py-1.5 text-xs font-semibold hover:text-text">I&apos;m free</button>
            </div>
            {busy.until && new Date(busy.until) > new Date() && <span className="mt-1 block text-xs text-muted">Busy until {untilText(busy.until)}.</span>}
          </div>

          <label className="block text-sm text-muted">
            Where you are (timezone)
            <select className={`${inputClass} mt-1`} value={tz} onChange={(e) => void save({ timezone: e.target.value })}>
              {timezones(tz).map((z) => <option key={z} value={z}>{z.replace(/_/g, " ")}</option>)}
            </select>
            <span className="mt-1 block text-xs">Daily busy hours follow this timezone. Change it when you travel.</span>
          </label>

          <div className="grid gap-4 sm:grid-cols-2">
            <label className="block text-sm text-muted">Busy from
              <input type="time" className={`${inputClass} mt-1`} value={busy.start} onChange={(e) => e.target.value && void save({ start: e.target.value })} />
            </label>
            <label className="block text-sm text-muted">Busy until
              <input type="time" className={`${inputClass} mt-1`} value={busy.end} onChange={(e) => e.target.value && void save({ end: e.target.value })} />
            </label>
          </div>

          <div className="text-sm text-muted">
            <span className="mb-1 block">Busy days <span className="text-xs">(none = only when you start a busy period yourself)</span></span>
            <div className="flex gap-1">
              {DAYS.map((name, i) => {
                const on = busy.days.includes(i);
                return (
                  <button key={name} type="button" aria-pressed={on} aria-label={name}
                    onClick={() => void save({ days: on ? busy.days.filter((d) => d !== i) : [...busy.days, i].sort() })}
                    className={`h-8 w-8 rounded-full text-xs font-semibold ${on ? "bg-accent text-accent-contrast" : "bg-surface-2 text-muted"}`}>
                    {name[0]}
                  </button>
                );
              })}
            </div>
          </div>

          <label className="block text-sm text-muted">
            What to tell people (optional)
            <input
              className={`${inputClass} mt-1`}
              maxLength={120}
              placeholder="e.g. in a meeting, driving, at the gym"
              value={note}
              onChange={(e) => { setNoteText(e.target.value); setSaved(false); }}
              onBlur={() => { if (noteText !== null) void save({ note: noteText }); }}
            />
            <span className="mt-1 block text-xs">Replies can say this, e.g. &ldquo;my boss is in a meeting&rdquo;. Leave empty to share nothing. Quick tip: in your own chat, &ldquo;busy for 2 hours - in a meeting&rdquo; sets it too.</span>
          </label>

          <div className="text-sm text-muted">
            <span className="mb-1 block">Reply while busy</span>
            <div className="flex gap-2">
              {([["Ai", "AI reply"], ["Fixed", "Fixed message"]] as const).map(([mode, label]) => (
                <button key={mode} type="button" aria-pressed={busy.replyMode === mode} onClick={() => void save({ replyMode: mode })}
                  className={`rounded-full px-4 py-1.5 text-sm font-semibold ${busy.replyMode === mode ? "bg-accent text-accent-contrast" : "bg-surface-2 text-muted"}`}>{label}</button>
              ))}
            </div>
            <span className="mt-1 block text-xs">
              {busy.replyMode === "Ai" ? "The assistant says it\u2019s your AI assistant and replies to greetings and thanks; anything that needs you is passed on to you." : "Everyone who writes gets exactly this message."}
            </span>
          </div>

          {busy.replyMode === "Fixed" && (
            <div>
              <textarea className={inputClass} rows={3} maxLength={1000} placeholder="e.g. I\u2019m busy right now and will get back to you soon." value={text} onChange={(e) => { setMessage(e.target.value); setSaved(false); }} />
              <button type="button" className={`${buttonClass} mt-2`} onClick={async () => { await save({ fixedMessage: text }); setSaved(true); }}>Save message</button>
              {saved && <span role="status" className="ml-3 text-sm text-muted">Saved.</span>}
            </div>
          )}
        </>
      )}
    </div>
  );
}
