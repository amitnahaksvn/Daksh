/**
 * Appearance preferences shared by the web shell.
 * Accent accepts named presets or a validated #RRGGBB custom colour.
 */
export type ThemePref = "system" | "light" | "dark";
export type StylePref = "bento" | "neural" | "aurora";
export type AccentPref = string;
export const THEME_KEY = "daksh.theme";
export const STYLE_KEY = "daksh.style";
export const ACCENT_KEY = "daksh.accent";
const PRESETS = ["violet", "blue", "emerald", "rose", "amber"];
export const isHexColour = (value: string): boolean => /^#[0-9a-fA-F]{6}$/.test(value);
const normaliseAccent = (value: string | null): AccentPref =>
  value && (PRESETS.includes(value) || isHexColour(value)) ? value.toLowerCase() : "violet";
export const themeInitScript = `(function(){try{var v=localStorage.getItem("${STYLE_KEY}");var s=v==="neural"||v==="aurora"?v:"bento";var a=localStorage.getItem("${ACCENT_KEY}")||"violet";if(!(["violet","blue","emerald","rose","amber"].includes(a)||/^#[0-9a-fA-F]{6}$/.test(a)))a="violet";var p=localStorage.getItem("${THEME_KEY}");var d=s!=="bento"||p==="dark"||((p===null||p==="system")&&window.matchMedia("(prefers-color-scheme: dark)").matches);var h=document.documentElement;h.dataset.style=s;h.dataset.accent=/^#/.test(a)?"custom":a;if(/^#/.test(a))h.style.setProperty("--palette",a);h.classList.toggle("dark",d)}catch(e){}})()`;
function read(key: string): string | null { try { return window.localStorage.getItem(key); } catch { return null; } }
export function readThemePref(): ThemePref { const value = read(THEME_KEY); return value === "light" || value === "dark" ? value : "system"; }
export function readStylePref(): StylePref { const value = read(STYLE_KEY); return value === "neural" || value === "aurora" ? value : "bento"; }
export function readAccentPref(): AccentPref { return normaliseAccent(read(ACCENT_KEY)); }
export function applyAppearance() {
 const style=readStylePref(); const accent=readAccentPref(); const pref=readThemePref(); const systemDark=window.matchMedia("(prefers-color-scheme: dark)").matches;
 const root=document.documentElement; root.dataset.style=style; root.dataset.accent=isHexColour(accent)?"custom":accent;
 if(isHexColour(accent)) root.style.setProperty("--palette",accent); else root.style.removeProperty("--palette");
 root.classList.toggle("dark",style!=="bento"||pref==="dark"||(pref==="system"&&systemDark));
}
const listeners=new Set<()=>void>(); const notify=()=>listeners.forEach(listener=>listener());
function write(key:string,value:string){try{window.localStorage.setItem(key,value)}catch{/* Apply for this visit if storage is unavailable. */}applyAppearance();notify()}
export const setThemePref=(pref:ThemePref)=>write(THEME_KEY,pref);
export const setStylePref=(style:StylePref)=>write(STYLE_KEY,style);
export const setAccentPref=(accent:AccentPref)=>{if(PRESETS.includes(accent)||isHexColour(accent))write(ACCENT_KEY,accent.toLowerCase())};
export function subscribeAppearance(onChange:()=>void){
 listeners.add(onChange);
 const onStorage=(event:StorageEvent)=>{if(event.key===THEME_KEY||event.key===STYLE_KEY||event.key===ACCENT_KEY){applyAppearance();onChange()}};
 const media=window.matchMedia("(prefers-color-scheme: dark)"); const onSystem=()=>applyAppearance();
 window.addEventListener("storage",onStorage);media.addEventListener("change",onSystem);
 return()=>{listeners.delete(onChange);window.removeEventListener("storage",onStorage);media.removeEventListener("change",onSystem)};
}
