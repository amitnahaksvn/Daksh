/**
 * Centralized appearance preferences. Custom accents are validated hex values and stored locally.
 */
export type ThemePref = "system" | "light" | "dark";
export type StylePref = "bento" | "neural" | "aurora";
export type AccentPref = "violet" | "blue" | "emerald" | "rose" | "amber";
export const THEME_KEY = "daksh.theme";
export const STYLE_KEY = "daksh.style";
export const ACCENT_KEY = "daksh.accent";
export const CUSTOM_ACCENT_KEY = "daksh.customAccent";
const HEX = /^#[0-9a-fA-F]{6}$/;
export const isValidHex = (value: string) => HEX.test(value);
export const themeInitScript = `(function(){try{var v=localStorage.getItem("${STYLE_KEY}");var s=v==="neural"||v==="aurora"?v:"bento";var a=localStorage.getItem("${ACCENT_KEY}")||"violet";var c=localStorage.getItem("${CUSTOM_ACCENT_KEY}");var p=localStorage.getItem("${THEME_KEY}");var d=s!=="bento"||p==="dark"||((p===null||p==="system")&&window.matchMedia("(prefers-color-scheme: dark)").matches);var h=document.documentElement;h.dataset.style=s;h.dataset.accent=a;if(c&&/^#[0-9a-fA-F]{6}$/.test(c))h.style.setProperty("--custom-accent",c);h.classList.toggle("dark",d)}catch(e){}})()`;
function read(key: string): string | null { try { return window.localStorage.getItem(key); } catch { return null; } }
export function readThemePref(): ThemePref { const v=read(THEME_KEY); return v==="light"||v==="dark"?v:"system"; }
export function readStylePref(): StylePref { const v=read(STYLE_KEY); return v==="neural"||v==="aurora"?v:"bento"; }
export function readAccentPref(): AccentPref { const v=read(ACCENT_KEY); return v==="blue"||v==="emerald"||v==="rose"||v==="amber"?v:"violet"; }
export function readCustomAccent(): string { const v=read(CUSTOM_ACCENT_KEY); return v&&isValidHex(v)?v:"#8B5CF6"; }
export function applyAppearance() {
 const style=readStylePref(), accent=readAccentPref(), custom=read(CUSTOM_ACCENT_KEY), pref=readThemePref();
 const root=document.documentElement, systemDark=window.matchMedia("(prefers-color-scheme: dark)").matches;
 root.dataset.style=style; root.dataset.accent=accent;
 if(custom&&isValidHex(custom)) root.style.setProperty("--custom-accent",custom); else root.style.removeProperty("--custom-accent");
 root.classList.toggle("dark",style!=="bento"||pref==="dark"||(pref==="system"&&systemDark));
}
const listeners=new Set<()=>void>(); const notify=()=>listeners.forEach(fn=>fn());
function write(key:string,value:string){try{window.localStorage.setItem(key,value)}catch{}applyAppearance();notify()}
export const setThemePref=(v:ThemePref)=>write(THEME_KEY,v);
export const setStylePref=(v:StylePref)=>write(STYLE_KEY,v);
export const setAccentPref=(v:AccentPref)=>write(ACCENT_KEY,v);
export const setCustomAccent=(v:string)=>{if(isValidHex(v))write(CUSTOM_ACCENT_KEY,v.toUpperCase())};
export function subscribeAppearance(onChange:()=>void){
 listeners.add(onChange);
 const onStorage=(e:StorageEvent)=>{if([THEME_KEY,STYLE_KEY,ACCENT_KEY,CUSTOM_ACCENT_KEY].includes(e.key||"")){applyAppearance();onChange()}};
 const media=window.matchMedia("(prefers-color-scheme: dark)"), onSystem=()=>{applyAppearance();onChange()};
 window.addEventListener("storage",onStorage);media.addEventListener("change",onSystem);
 return()=>{listeners.delete(onChange);window.removeEventListener("storage",onStorage);media.removeEventListener("change",onSystem)};
}
