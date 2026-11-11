/**
 * Appearance has two independent choices:
 *  - style: "bento" (soft tiles, Day / Night) or "neural" (the AI look: aurora, glass, glow; always Night)
 *  - mode: Day, Night or Auto (only for Bento; Neural is a night look by design)
 * Both are remembered per browser.
 */
export type ThemePref = "system" | "light" | "dark";
export type StylePref = "bento" | "neural";

export const THEME_KEY = "daksh.theme";
export const STYLE_KEY = "daksh.style";

/** Runs in <head> before the page paints, so the saved choice shows from the first frame (no flash of the wrong look). */
export const themeInitScript = `(function(){try{var s=localStorage.getItem("${STYLE_KEY}")==="neural"?"neural":"bento";var p=localStorage.getItem("${THEME_KEY}");var d=s==="neural"||p==="dark"||((p===null||p==="system")&&window.matchMedia("(prefers-color-scheme: dark)").matches);var h=document.documentElement;h.dataset.style=s;h.classList.toggle("dark",d)}catch(e){}})()`;

function read(key: string): string | null {
  try {
    return window.localStorage.getItem(key);
  } catch {
    // Storage can be blocked (private mode). Fall back to the defaults.
    return null;
  }
}

export function readThemePref(): ThemePref {
  const value = read(THEME_KEY);
  return value === "light" || value === "dark" ? value : "system";
}

export function readStylePref(): StylePref {
  return read(STYLE_KEY) === "neural" ? "neural" : "bento";
}

export function applyAppearance() {
  const style = readStylePref();
  const pref = readThemePref();
  const systemDark = window.matchMedia("(prefers-color-scheme: dark)").matches;
  const root = document.documentElement;
  root.dataset.style = style;
  root.classList.toggle("dark", style === "neural" || pref === "dark" || (pref === "system" && systemDark));
}

const listeners = new Set<() => void>();
const notify = () => listeners.forEach((l) => l());

function write(key: string, value: string) {
  try {
    window.localStorage.setItem(key, value);
  } catch {
    // Not saved, but still applied for this visit.
  }
  applyAppearance();
  notify();
}

export const setThemePref = (pref: ThemePref) => write(THEME_KEY, pref);
export const setStylePref = (style: StylePref) => write(STYLE_KEY, style);

/** For useSyncExternalStore: re-read when a choice changes here or in another tab, and keep "system" in step with the OS. */
export function subscribeAppearance(onChange: () => void) {
  listeners.add(onChange);
  const onStorage = (e: StorageEvent) => {
    if (e.key === THEME_KEY || e.key === STYLE_KEY) {
      applyAppearance();
      onChange();
    }
  };
  const media = window.matchMedia("(prefers-color-scheme: dark)");
  const onSystem = () => applyAppearance();
  window.addEventListener("storage", onStorage);
  media.addEventListener("change", onSystem);
  return () => {
    listeners.delete(onChange);
    window.removeEventListener("storage", onStorage);
    media.removeEventListener("change", onSystem);
  };
}
