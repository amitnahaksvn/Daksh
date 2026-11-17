"use client";
import { useEffect, useRef, useState } from "react";
import { Dropdown, errorInputClass, inputClass } from "@/components/ui";

const MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
const WEEKDAYS = ["Su", "Mo", "Tu", "We", "Th", "Fr", "Sa"];

const pad = (n: number) => String(n).padStart(2, "0");
const toIso = (y: number, m: number, d: number) => `${y}-${pad(m + 1)}-${pad(d)}`;
function parseIso(v: string) {
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(v);
  return m ? { y: +m[1], m: +m[2] - 1, d: +m[3] } : null;
}
function todayIso() {
  const t = new Date();
  return toIso(t.getFullYear(), t.getMonth(), t.getDate());
}
function format(v: string) {
  const p = parseIso(v);
  return p ? `${pad(p.d)} ${MONTHS[p.m].slice(0, 3)} ${p.y}` : "";
}

/** A themed date picker (the native date popup ignores the palette). Values are `YYYY-MM-DD` strings, so there
 *  are no timezone surprises. `min`/`max` are `YYYY-MM-DD` too; `max` defaults to today. */
export function DatePicker({
  value,
  onChange,
  min = "1900-01-01",
  max,
  placeholder = "Select date",
  invalid = false,
  className = "w-full",
}: {
  value: string;
  onChange: (v: string) => void;
  min?: string;
  max?: string;
  placeholder?: string;
  invalid?: boolean;
  className?: string;
}) {
  // "Today" is read after mount: evaluating the clock while the page is pre-rendered would freeze the build date.
  const [today, setToday] = useState<string | null>(null);
  useEffect(() => {
    const t = setTimeout(() => setToday(todayIso()), 0);
    return () => clearTimeout(t);
  }, []);
  const maxIso = max ?? today ?? "9999-12-31";
  const [open, setOpen] = useState(false);
  const [view, setView] = useState({ y: 0, m: 0 });
  const rootRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (!rootRef.current?.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && setOpen(false);
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  const minP = parseIso(min)!;
  const maxP = parseIso(maxIso)!;

  function toggle() {
    if (!open) {
      // Open on the selected date's month, else the latest allowed month (sensible for birthdays).
      const p = parseIso(value) ?? maxP;
      setView({ y: p.y, m: p.m });
    }
    setOpen((o) => !o);
  }

  // Clamp the visible month into [min, max] so prev/next and the dropdowns can't leave the valid range.
  const clamp = (y: number, m: number) => {
    const key = y * 12 + m;
    const lo = minP.y * 12 + minP.m;
    const hi = maxP.y * 12 + maxP.m;
    const k = Math.min(Math.max(key, lo), hi);
    return { y: Math.floor(k / 12), m: k % 12 };
  };
  const shift = (delta: number) => setView((v) => clamp(v.y, v.m + delta));
  const canPrev = view.y * 12 + view.m > minP.y * 12 + minP.m;
  const canNext = view.y * 12 + view.m < maxP.y * 12 + maxP.m;

  const years = Array.from({ length: maxP.y - minP.y + 1 }, (_, i) => maxP.y - i);
  const firstWeekday = new Date(view.y, view.m, 1).getDay();
  const daysInMonth = new Date(view.y, view.m + 1, 0).getDate();
  const cells: (number | null)[] = [...Array(firstWeekday).fill(null), ...Array.from({ length: daysInMonth }, (_, i) => i + 1)];

  return (
    <div ref={rootRef} className={`relative ${className}`}>
      <button
        type="button"
        aria-haspopup="dialog"
        aria-expanded={open}
        onClick={toggle}
        className={`${inputClass} flex items-center justify-between gap-2 text-left focus-visible:outline-2 focus-visible:outline-accent ${invalid ? errorInputClass : ""}`}
      >
        <span className={value ? "" : "text-muted"}>{format(value) || placeholder}</span>
        <svg aria-hidden viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" className="h-4 w-4 shrink-0 text-muted">
          <rect x="3.5" y="5" width="17" height="15.5" rx="3" />
          <path d="M8 3v4M16 3v4M3.5 10h17" />
        </svg>
      </button>

      {open && (
        <div role="dialog" aria-label="Choose date" className="absolute left-0 z-30 mt-1 w-80 max-w-[90vw] rounded-2xl border border-line bg-surface p-3 shadow-lg">
          <div className="mb-2 flex items-center gap-1">
            <NavButton label="Previous month" disabled={!canPrev} onClick={() => shift(-1)} dir="left" />
            <Dropdown
              className="min-w-0 flex-1 [&_button]:!px-3"
              searchable={false}
              value={String(view.m)}
              onChange={(v) => setView((cur) => clamp(cur.y, +v))}
              options={MONTHS.map((label, i) => ({ value: String(i), label }))}
            />
            <Dropdown
              className="w-[5.5rem] shrink-0 [&_button]:!px-3"
              value={String(view.y)}
              onChange={(v) => setView((cur) => clamp(+v, cur.m))}
              options={years.map((y) => ({ value: String(y), label: String(y) }))}
            />
            <NavButton label="Next month" disabled={!canNext} onClick={() => shift(1)} dir="right" />
          </div>

          <div className="grid grid-cols-7 text-center text-xs font-semibold text-muted">
            {WEEKDAYS.map((d) => <span key={d} className="py-1">{d}</span>)}
          </div>
          <div className="grid grid-cols-7 gap-y-0.5 text-center text-sm">
            {cells.map((day, i) => {
              if (day === null) return <span key={`b${i}`} />;
              const iso = toIso(view.y, view.m, day);
              const disabled = iso < min || iso > maxIso;
              const selected = iso === value;
              const today = iso === todayIso();
              return (
                <button
                  key={iso}
                  type="button"
                  disabled={disabled}
                  aria-pressed={selected}
                  onClick={() => { onChange(iso); setOpen(false); }}
                  className={`mx-auto grid size-9 place-items-center rounded-full transition-colors disabled:cursor-not-allowed disabled:opacity-30 ${
                    selected
                      ? "bg-accent font-semibold text-accent-contrast"
                      : `hover:bg-surface-2 ${today ? "border border-accent font-semibold" : ""}`
                  }`}
                >
                  {day}
                </button>
              );
            })}
          </div>
        </div>
      )}
    </div>
  );
}

function NavButton({ label, disabled, onClick, dir }: { label: string; disabled: boolean; onClick: () => void; dir: "left" | "right" }) {
  return (
    <button
      type="button"
      aria-label={label}
      disabled={disabled}
      onClick={onClick}
      className="grid size-8 shrink-0 place-items-center rounded-full text-muted transition-colors hover:bg-surface-2 hover:text-text disabled:opacity-30"
    >
      <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4" aria-hidden>
        <path d={dir === "left" ? "M10 4l-4 4 4 4" : "M6 4l4 4-4 4"} stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    </button>
  );
}
