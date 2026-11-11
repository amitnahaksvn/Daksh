"use client";
import { useSyncExternalStore } from "react";
import { readStylePref, readThemePref, setStylePref, setThemePref, subscribeAppearance, type StylePref, type ThemePref } from "@/lib/theme";

const MODES: { value: ThemePref; label: string }[] = [
  { value: "light", label: "Day" },
  { value: "dark", label: "Night" },
  { value: "system", label: "Auto" },
];

const STYLES: { value: StylePref; label: string }[] = [
  { value: "bento", label: "Bento" },
  { value: "neural", label: "Neural" },
];

function Segmented<T extends string>({ label, options, value, onChange, className = "" }: { label: string; options: { value: T; label: string }[]; value: T; onChange: (v: T) => void; className?: string }) {
  return (
    <div role="radiogroup" aria-label={label} className={`inline-flex rounded-full bg-surface-2 p-1 ${className}`}>
      {options.map((o) => (
        <button
          key={o.value}
          type="button"
          role="radio"
          aria-checked={value === o.value}
          onClick={() => onChange(o.value)}
          className={`rounded-full px-3.5 py-1.5 text-xs font-semibold transition-colors ${value === o.value ? "bg-accent text-accent-contrast" : "text-muted hover:text-text"}`}
        >
          {o.label}
        </button>
      ))}
    </div>
  );
}

/** Day / Night / Auto. Hidden while the Neural style is on: that look is always Night. */
export function ThemeToggle({ className = "" }: { className?: string }) {
  // The server cannot know the saved choice, so it renders the defaults; the browser then reads the real ones.
  const pref = useSyncExternalStore(subscribeAppearance, readThemePref, () => "system" as ThemePref);
  const style = useSyncExternalStore(subscribeAppearance, readStylePref, () => "bento" as StylePref);
  if (style === "neural") return null;
  return <Segmented label="Appearance" options={MODES} value={pref} onChange={setThemePref} className={className} />;
}

/** Bento (soft tiles) or Neural (the AI look). */
export function StyleToggle({ className = "" }: { className?: string }) {
  const style = useSyncExternalStore(subscribeAppearance, readStylePref, () => "bento" as StylePref);
  return <Segmented label="Style" options={STYLES} value={style} onChange={setStylePref} className={className} />;
}

/** Both controls together, for the sidebar and the phone header. */
export function AppearanceControls({ className = "" }: { className?: string }) {
  return (
    <div className={`flex flex-wrap items-center gap-2 ${className}`}>
      <StyleToggle />
      <ThemeToggle />
    </div>
  );
}
