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

/** A `<select>` styled to match `inputClass`, with its own chevron (native dropdown arrows don't follow the theme). */
export function Select({
  invalid,
  className = "",
  children,
  ...props
}: React.SelectHTMLAttributes<HTMLSelectElement> & { invalid?: boolean }) {
  return (
    <div className="relative">
      <select
        {...props}
        className={`${inputClass} appearance-none pr-10 ${props.value ? "" : "text-muted"} ${invalid ? errorInputClass : ""} ${className}`}
      >
        {children}
      </select>
      <svg
        aria-hidden
        viewBox="0 0 16 16"
        fill="none"
        className="pointer-events-none absolute right-4 top-1/2 h-4 w-4 -translate-y-1/2 text-muted"
      >
        <path d="M4 6l4 4 4-4" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    </div>
  );
}
