/**
 * Appearance preferences shared by the web shell.
 *
 * A *style* (theme) is the whole look: colours, type, shape, app shell and dashboard hero. *Mode* is day / night / auto
 * and applies to every style.
 */
export type ThemePref = "system" | "light" | "dark";
export type StyleId = "sandhya" | "prana" | "yantra" | "hybrid" | "rangoli" | "ratna" | "galaxy" | "mitra" | "nakshatra" | "kavach";
export type StylePref = StyleId;
/** The recommended theme, used until the user picks one. */
export const DEFAULT_STYLE: StyleId = "hybrid";
/** How the app frame is arranged: a full sidebar, a slim rail, or a top bar. Phones always get a bottom tab bar. */
export type ShellKind = "sidebar" | "rail" | "top";
/** The person's own choice of frame. "auto" means: use the theme's own. */
export type LayoutPref = "auto" | ShellKind;
export const LAYOUTS: { value: LayoutPref; label: string; hint: string }[] = [
  { value: "auto", label: "Theme default", hint: "The frame this theme was designed with" },
  { value: "sidebar", label: "Sidebar", hint: "Menu down the left side" },
  { value: "rail", label: "Slim sidebar", hint: "A narrow rail with small labels" },
  { value: "top", label: "Top bar", hint: "Menu across the top" },
];

export interface ThemeInfo {
  id: StyleId;
  label: string;
  tagline: string;
  /** Preview swatches: [canvas, surface, accent] for day and night. */
  day: [string, string, string];
  night: [string, string, string];
  /** Google Fonts css2 query (without the base URL) loaded only while the style is active. */
  fonts?: string;
  shell: ShellKind;
}

export const THEMES: ThemeInfo[] = [
  { id: "sandhya", label: "Sandhya", tagline: "Warm editorial briefing", day: ["#f3eada", "#fffbf3", "#e8812b"], night: ["#0f0e1f", "#1b1938", "#f2994a"], fonts: "family=Fraunces:opsz,wght@9..144,500;9..144,600&family=DM+Sans:wght@400;500;600;700", shell: "sidebar" },
  { id: "prana", label: "Prana", tagline: "Calm and conversation first", day: ["#e3eeeb", "#ffffff", "#0f766e"], night: ["#070d13", "#0e181e", "#5eead4"], fonts: "family=Sora:wght@400;500;600;700&family=Manrope:wght@400;500;600;700", shell: "top" },
  { id: "yantra", label: "Yantra", tagline: "Modular instrument panel", day: ["#f3f1ec", "#ffffff", "#e5402a"], night: ["#14161a", "#1d2026", "#ff6a52"], fonts: "family=Space+Grotesk:wght@500;600;700&family=IBM+Plex+Mono:wght@400;500&family=Inter:wght@400;500;600;700", shell: "top" },
  { id: "hybrid", label: "Sandhya × Yantra", tagline: "Warm and structured (recommended)", day: ["#f0e7d8", "#fffbf3", "#e8812b"], night: ["#0d0c22", "#1e1c3e", "#f2994a"], fonts: "family=Fraunces:opsz,wght@9..144,500;9..144,600&family=DM+Sans:wght@400;500;600;700&family=IBM+Plex+Mono:wght@400;500", shell: "sidebar" },
  { id: "rangoli", label: "Rangoli", tagline: "Bold colour blocks", day: ["#fff4d6", "#ffffff", "#f0447b"], night: ["#150f33", "#241a55", "#ff5c92"], fonts: "family=Bricolage+Grotesque:opsz,wght@12..96,500;12..96,700;12..96,800&family=DM+Sans:wght@400;500;600;700", shell: "top" },
  { id: "ratna", label: "Ratna", tagline: "Dark ink and gold", day: ["#f3ede0", "#fbf7ee", "#8a5f12"], night: ["#0b0b12", "#12121c", "#d9b26a"], fonts: "family=Cormorant+Garamond:wght@500;600;700&family=Inter:wght@400;500;600;700", shell: "rail" },
  { id: "galaxy", label: "Galaxy", tagline: "Nebula, glass and orbits", day: ["#f5f1ff", "#ffffff", "#5b3fd0"], night: ["#07061a", "#1e1646", "#c4b5fd"], fonts: "family=Unbounded:wght@400;500;600&family=Inter:wght@400;500;600;700", shell: "top" },
  { id: "nakshatra", label: "Nakshatra", tagline: "Star-map night sky", day: ["#eaf0fb", "#ffffff", "#c2410c"], night: ["#070b1a", "#0f1530", "#ffc857"], fonts: "family=Syne:wght@600;700;800&family=Inter:wght@400;500;600;700", shell: "rail" },
  { id: "mitra", label: "Mitra", tagline: "Friendly and rounded", day: ["#e6f6ee", "#ffffff", "#12a37a"], night: ["#12202a", "#1b2d3a", "#4adeb1"], fonts: "family=Baloo+2:wght@500;600;700;800&family=Nunito:wght@400;600;700", shell: "top" },
  { id: "kavach", label: "Kavach", tagline: "Trust and control", day: ["#eef3f1", "#ffffff", "#1f8a5b"], night: ["#0a1512", "#12211d", "#3ddc97"], fonts: "family=Outfit:wght@500;600;700&family=Inter:wght@400;500;600;700", shell: "sidebar" },
];

const STYLE_IDS = THEMES.map((t) => t.id) as StyleId[];
const FONT_QUERIES = Object.fromEntries(THEMES.filter((t) => t.fonts).map((t) => [t.id, t.fonts as string]));
const FONT_LINK_ID = "daksh-theme-fonts";
const FONT_BASE = "https://fonts.googleapis.com/css2?";

export const THEME_KEY = "daksh.theme";
export const STYLE_KEY = "daksh.style";
export const LAYOUT_KEY = "daksh.layout";
export const themeInfo = (id: StyleId): ThemeInfo => THEMES.find((t) => t.id === id) ?? THEMES.find((t) => t.id === DEFAULT_STYLE) ?? THEMES[0];

/** Runs before first paint (inline in <head>) so the chosen theme never flashes. Keep it dependency-free. */
export const themeInitScript = `(function(){try{var S=${JSON.stringify(STYLE_IDS)},F=${JSON.stringify(FONT_QUERIES)};var v=localStorage.getItem("${STYLE_KEY}");var s=S.indexOf(v)>-1?v:"${DEFAULT_STYLE}";var p=localStorage.getItem("${THEME_KEY}");var d=p==="dark"||((p===null||p==="system")&&window.matchMedia("(prefers-color-scheme: dark)").matches);var h=document.documentElement;h.dataset.style=s;h.classList.toggle("dark",d);if(F[s]){var l=document.createElement("link");l.id="${FONT_LINK_ID}";l.rel="stylesheet";l.href="${FONT_BASE}"+F[s]+"&display=swap";document.head.appendChild(l)}}catch(e){}})()`;

function read(key: string): string | null { try { return window.localStorage.getItem(key); } catch { return null; } }
export function readThemePref(): ThemePref { const value = read(THEME_KEY); return value === "light" || value === "dark" ? value : "system"; }
export function readLayoutPref(): LayoutPref { const value = read(LAYOUT_KEY); return value === "sidebar" || value === "rail" || value === "top" ? value : "auto"; }
/** The frame actually used: the person's choice, else the theme's own. */
export const effectiveShell = (style: StyleId, layout: LayoutPref): ShellKind => (layout === "auto" ? themeInfo(style).shell : layout);
export function readStylePref(): StylePref { const value = read(STYLE_KEY); return (STYLE_IDS as string[]).includes(value ?? "") ? (value as StyleId) : DEFAULT_STYLE; }

function applyFonts(style: StyleId) {
  const query = FONT_QUERIES[style];
  const existing = document.getElementById(FONT_LINK_ID) as HTMLLinkElement | null;
  if (!query) { existing?.remove(); return; }
  const href = `${FONT_BASE}${query}&display=swap`;
  if (existing) { if (existing.href !== href) existing.href = href; return; }
  const link = document.createElement("link");
  link.id = FONT_LINK_ID; link.rel = "stylesheet"; link.href = href;
  document.head.appendChild(link);
}

export function applyAppearance() {
  const style = readStylePref(); const pref = readThemePref();
  const systemDark = window.matchMedia("(prefers-color-scheme: dark)").matches;
  const root = document.documentElement; root.dataset.style = style;
  root.classList.toggle("dark", pref === "dark" || (pref === "system" && systemDark));
  applyFonts(style);
}
const listeners = new Set<() => void>(); const notify = () => listeners.forEach((listener) => listener());
function write(key: string, value: string) { try { window.localStorage.setItem(key, value); } catch { /* Apply for this visit if storage is unavailable. */ } applyAppearance(); notify(); }
export const setThemePref = (pref: ThemePref) => write(THEME_KEY, pref);
export const setStylePref = (style: StylePref) => write(STYLE_KEY, style);
export const setLayoutPref = (layout: LayoutPref) => write(LAYOUT_KEY, layout);
export function subscribeAppearance(onChange: () => void) {
  listeners.add(onChange);
  const onStorage = (event: StorageEvent) => { if (event.key === THEME_KEY || event.key === STYLE_KEY || event.key === LAYOUT_KEY) { applyAppearance(); onChange(); } };
  const media = window.matchMedia("(prefers-color-scheme: dark)"); const onSystem = () => applyAppearance();
  window.addEventListener("storage", onStorage); media.addEventListener("change", onSystem);
  return () => { listeners.delete(onChange); window.removeEventListener("storage", onStorage); media.removeEventListener("change", onSystem); };
}
