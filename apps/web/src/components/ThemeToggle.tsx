"use client";
import { useState, useSyncExternalStore } from "react";
import { createPortal } from "react-dom";
import { LAYOUTS, readLayoutPref, readStylePref, readThemePref, setLayoutPref, setStylePref, setThemePref, subscribeAppearance, THEMES, themeInfo, DEFAULT_STYLE, type LayoutPref, type StylePref, type ThemePref } from "@/lib/theme";

const MODES: { value: ThemePref; label: string }[] = [{ value: "light", label: "Day" }, { value: "dark", label: "Night" }, { value: "system", label: "Auto" }];

/** The frame (sidebar, slim sidebar or top bar) the person chose; "auto" follows the theme. */
export function useLayout(): LayoutPref {
  return useSyncExternalStore(subscribeAppearance, readLayoutPref, () => "auto" as LayoutPref);
}

/** The theme the user is on (server renders the default; the browser corrects it right after load). */
export function useStyle(): StylePref {
  return useSyncExternalStore(subscribeAppearance, readStylePref, () => DEFAULT_STYLE);
}

function Segmented<T extends string>({ label, options, value, onChange, className = "" }: { label: string; options: { value: T; label: string }[]; value: T; onChange: (v: T) => void; className?: string }) {
  return (
    <div role="radiogroup" aria-label={label} className={`inline-flex rounded-full bg-surface-2 p-1 ${className}`}>
      {options.map((option) => (
        <button key={option.value} type="button" role="radio" aria-checked={value === option.value} onClick={() => onChange(option.value)} className={`rounded-full px-3.5 py-1.5 text-xs font-semibold transition-colors ${value === option.value ? "bg-accent text-accent-contrast" : "text-muted hover:text-text"}`}>
          {option.label}
        </button>
      ))}
    </div>
  );
}

/** Day / Night / Auto. Every theme has both looks. */
export function ThemeToggle({ className = "" }: { className?: string }) {
  const pref = useSyncExternalStore(subscribeAppearance, readThemePref, () => "system" as ThemePref);
  return <Segmented label="Appearance" options={MODES} value={pref} onChange={setThemePref} className={className} />;
}

function ThemeSwatch({ colors }: { colors: [string, string, string] }) {
  return (
    <span aria-hidden className="flex h-4 w-7 overflow-hidden rounded-full border border-line">
      <span className="flex-1" style={{ background: colors[0] }} /><span className="flex-1" style={{ background: colors[1] }} /><span className="flex-1" style={{ background: colors[2] }} />
    </span>
  );
}

function LayoutPicker() {
  const layout = useLayout();
  return (
    <div className="mb-4">
      <p className="mb-2 text-sm font-semibold">Layout</p>
      <div role="radiogroup" aria-label="Layout" className="grid grid-cols-2 gap-2 sm:grid-cols-4">
        {LAYOUTS.map((l) => (
          <button key={l.value} type="button" role="radio" aria-checked={layout === l.value} onClick={() => setLayoutPref(l.value)} className={`rounded-xl border-2 px-3 py-2 text-left transition-colors ${layout === l.value ? "border-accent" : "border-line hover:border-muted"}`}>
            <span className="mb-1.5 flex h-7 gap-1 overflow-hidden rounded-md border border-line bg-surface-2 p-1" aria-hidden>
              {l.value === "top" ? <span className="flex w-full flex-col gap-1"><span className="h-1.5 rounded-sm bg-accent" /><span className="flex-1 rounded-sm bg-line" /></span>
                : l.value === "rail" ? <><span className="w-1.5 rounded-sm bg-accent" /><span className="flex-1 rounded-sm bg-line" /></>
                : l.value === "sidebar" ? <><span className="w-4 rounded-sm bg-accent" /><span className="flex-1 rounded-sm bg-line" /></>
                : <><span className="w-3 rounded-sm bg-accent opacity-60" /><span className="flex-1 rounded-sm bg-line" /></>}
            </span>
            <span className="block text-xs font-bold">{l.label}</span>
            <span className="block text-[11px] leading-tight text-muted">{l.hint}</span>
          </button>
        ))}
      </div>
    </div>
  );
}

function ThemeDialog({ onClose, style, pref }: { onClose: () => void; style: StylePref; pref: ThemePref }) {
  const systemDark = typeof window !== "undefined" && window.matchMedia?.("(prefers-color-scheme: dark)").matches;
  const night = pref === "dark" || (pref === "system" && systemDark);
  return createPortal(
    <div role="dialog" aria-modal="true" aria-label="Choose a theme" className="fixed inset-0 z-[1000] flex items-end justify-center bg-black/50 p-0 sm:items-center sm:p-6" onClick={onClose} onKeyDown={(event) => { if (event.key === "Escape") onClose(); }}>
      <div className="max-h-[90vh] w-full max-w-3xl overflow-y-auto rounded-t-3xl bg-canvas p-5 text-text shadow-2xl sm:rounded-3xl" onClick={(event) => event.stopPropagation()}>
        <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
          <div><h2 className="text-xl font-bold">Appearance</h2><p className="text-sm text-muted">Pick a theme and a layout. Every theme has a day and a night look, and works with any layout.</p></div>
          <div className="flex items-center gap-2"><ThemeToggle /><button type="button" onClick={onClose} className="rounded-full bg-accent px-4 py-1.5 text-xs font-semibold text-accent-contrast">Done</button></div>
        </div>
        <LayoutPicker />
        <p className="mb-2 text-sm font-semibold">Theme</p>
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3" role="radiogroup" aria-label="Theme">
          {THEMES.map((theme) => {
            const selected = style === theme.id; const c = night ? theme.night : theme.day;
            return (
              <button key={theme.id} type="button" role="radio" aria-checked={selected} onClick={() => setStylePref(theme.id)} className={`rounded-2xl border-2 p-3 text-left transition-colors ${selected ? "border-accent" : "border-line hover:border-muted"}`}>
                <div className="mb-3 flex h-16 overflow-hidden rounded-xl border border-line" style={{ background: c[0] }}>
                  <div className="m-2 flex-1 rounded-lg" style={{ background: c[1] }} /><div className="my-2 mr-2 w-1/4 rounded-lg" style={{ background: c[2] }} />
                </div>
                <div className="text-sm font-bold">{theme.label}{selected && <span className="ml-2 text-xs font-semibold text-accent">Selected</span>}</div>
                <div className="text-xs text-muted">{theme.tagline}</div>
              </button>
            );
          })}
        </div>
      </div>
    </div>,
    document.body,
  );
}

/** A button showing the current theme that opens the picker. */
export function StyleToggle({ className = "" }: { className?: string }) {
  const style = useStyle();
  const pref = useSyncExternalStore(subscribeAppearance, readThemePref, () => "system" as ThemePref);
  const [open, setOpen] = useState(false);
  const current = themeInfo(style);
  const night = pref === "dark";
  return (
    <div className={className}>
      <button type="button" aria-haspopup="dialog" aria-expanded={open} onClick={() => setOpen(true)} className="flex items-center gap-2 rounded-full border border-line bg-surface-2 px-3 py-1.5 text-xs font-semibold text-text">
        <ThemeSwatch colors={night ? current.night : current.day} />Theme: {current.label}
      </button>
      {open && <ThemeDialog onClose={() => setOpen(false)} style={style} pref={pref} />}
    </div>
  );
}

export function AppearanceControls({ className = "" }: { className?: string }) {
  return <div className={`flex flex-col items-start gap-2 ${className}`}><StyleToggle /><ThemeToggle /></div>;
}
