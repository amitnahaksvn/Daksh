"use client";
import { useEffect, useRef, useState } from "react";
import type { ReactNode } from "react";

/** The Bento building blocks: one corner radius, soft white tiles on the grey canvas, lime and pink accent tiles. */
export const tileClass = "tile rounded-[1.25rem] border border-line bg-surface shadow-[var(--shadow)]";
export const listClass = "tile divide-y divide-line overflow-hidden rounded-[1.25rem] border border-line bg-surface shadow-[var(--shadow)]";

export function PageHeader({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <header className={`mb-4 p-6 ${tileClass}`}>
      <h1 className="text-3xl font-extrabold">{title}</h1>
      {children && <p className="mt-1 text-sm text-muted">{children}</p>}
    </header>
  );
}

export function Loading({ label = "Loading" }: { label?: string }) {
  return (
    <p role="status" aria-live="polite" className="text-sm text-muted">
      {label}…
    </p>
  );
}

export function ErrorState({ error, onRetry }: { error: Error; onRetry?: () => void }) {
  return (
    <div role="alert" className="rounded-[1.25rem] bg-danger-bg p-5 text-danger">
      <p className="font-semibold">Something went wrong</p>
      <p className="mt-1 text-sm">{error.message}</p>
      {onRetry && (
        <button onClick={onRetry} className="mt-3 rounded-full border border-current px-4 py-1.5 text-sm font-semibold hover:bg-black/5 dark:hover:bg-white/10">
          Try again
        </button>
      )}
    </div>
  );
}

export function EmptyState({ children }: { children: ReactNode }) {
  return <p className="rounded-[1.25rem] border border-dashed border-line bg-surface p-8 text-center text-sm text-muted">{children}</p>;
}

/** A heads-up (for example "this is simulated"): a pink tile with ink text. */
export function Notice({ children }: { children: ReactNode }) {
  return <p className="mb-4 rounded-[1.25rem] bg-pink p-4 text-sm text-on-tint">{children}</p>;
}

/** A small status label. `tone="lime"` for a good state, `"pink"` for a simulated or pending one. */
export function Chip({ children, tone = "pink" }: { children: ReactNode; tone?: "lime" | "pink" | "plain" }) {
  const tones = { lime: "bg-lime text-on-tint", pink: "bg-pink text-on-tint", plain: "bg-surface-2 text-muted" };
  return <span className={`ai-mono inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-semibold ${tones[tone]}`}>{children}</span>;
}

export const buttonClass =
  "inline-flex items-center justify-center rounded-full bg-accent px-5 py-2.5 text-sm font-semibold text-accent-contrast transition-opacity hover:opacity-90 disabled:opacity-50";
export const secondaryButtonClass =
  "inline-flex items-center justify-center rounded-full border border-line bg-surface px-5 py-2.5 text-sm font-semibold text-text transition-colors hover:bg-surface-2";
export const inputClass =
  "w-full rounded-2xl border border-line bg-surface-2 px-4 py-2.5 text-sm text-text placeholder:text-muted";
/** Appended to `inputClass` (or `selectClass`) to flag an invalid field with a red border. */
export const errorInputClass = "!border-danger";

export type DropdownOption = { value: string; label: string };

/** The app's one dropdown. Native `<select>` popups ignore the theme, so this renders its own list in the
 *  palette's colours. Long lists (more than 8 options) get a search box; pass `searchable` to force either. */
export function Dropdown({
  value,
  options,
  onChange,
  className = "w-full",
  placeholder = "Select…",
  searchPlaceholder = "Search…",
  searchable,
  disabled = false,
  invalid = false,
}: {
  value: string;
  options: DropdownOption[];
  onChange: (v: string) => void;
  className?: string;
  placeholder?: string;
  searchPlaceholder?: string;
  searchable?: boolean;
  disabled?: boolean;
  invalid?: boolean;
}) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const rootRef = useRef<HTMLDivElement>(null);
  const listRef = useRef<HTMLUListElement>(null);
  const showSearch = searchable ?? options.length > 8;

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

  useEffect(() => {
    if (open) listRef.current?.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: "center" });
  }, [open]);

  const q = query.trim().toLowerCase();
  const shown = q ? options.filter((o) => o.label.toLowerCase().includes(q) || o.value.toLowerCase().includes(q)) : options;
  const currentLabel = options.find((o) => o.value === value)?.label;

  return (
    <div ref={rootRef} className={`relative ${className}`}>
      <button
        type="button"
        disabled={disabled}
        aria-haspopup="listbox"
        aria-expanded={open}
        onClick={() => { setQuery(""); setOpen((o) => !o); }}
        className={`${inputClass} flex items-center justify-between gap-2 text-left focus-visible:outline-2 focus-visible:outline-accent disabled:cursor-not-allowed ${invalid ? errorInputClass : ""}`}
      >
        <span className={`truncate ${currentLabel ? "" : "text-muted"}`}>{currentLabel ?? (value || placeholder)}</span>
        <svg aria-hidden viewBox="0 0 16 16" fill="none" className={`h-4 w-4 shrink-0 text-muted transition-transform ${open ? "rotate-180" : ""}`}>
          <path d="M4 6l4 4 4-4" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </button>
      {open && !disabled && (
        <div className="absolute left-0 z-30 mt-1 w-full min-w-44 rounded-2xl border border-line bg-surface p-2 shadow-lg">
          {showSearch && (
            <input
              autoFocus
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter" && shown[0]) { onChange(shown[0].value); setOpen(false); }
              }}
              placeholder={searchPlaceholder}
              className="mb-2 w-full rounded-xl border border-line bg-surface-2 px-3 py-2 text-sm text-text placeholder:text-muted"
            />
          )}
          <ul ref={listRef} role="listbox" className="max-h-60 overflow-y-auto">
            {shown.length === 0 && <li className="px-3 py-2 text-xs text-muted">No matches</li>}
            {shown.map((o) => {
              const selected = o.value === value;
              return (
                <li
                  key={o.value}
                  role="option"
                  aria-selected={selected}
                  onClick={() => { onChange(o.value); setOpen(false); }}
                  className={`cursor-pointer rounded-lg px-3 py-1.5 text-sm ${selected ? "bg-accent text-accent-contrast" : "hover:bg-surface-2"}`}
                >
                  {o.label}
                </li>
              );
            })}
          </ul>
        </div>
      )}
    </div>
  );
}
