"use client";
import { useSyncExternalStore } from "react";
import { readStylePref, readThemePref, setStylePref, setThemePref, subscribeAppearance, type StylePref, type ThemePref } from "@/lib/theme";
const MODES:{value:ThemePref;label:string}[]=[{value:"light",label:"Day"},{value:"dark",label:"Night"},{value:"system",label:"Auto"}];
const STYLES:{value:StylePref;label:string}[]=[{value:"bento",label:"Bento"},{value:"neural",label:"Neural"},{value:"aurora",label:"Aurora"}];
function Segmented<T extends string>({label,options,value,onChange,className=""}:{label:string;options:{value:T;label:string}[];value:T;onChange:(v:T)=>void;className?:string}){
 return <div role="radiogroup" aria-label={label} className={`inline-flex rounded-full bg-surface-2 p-1 ${className}`}>{options.map(option=><button key={option.value} type="button" role="radio" aria-checked={value===option.value} onClick={()=>onChange(option.value)} className={`rounded-full px-3.5 py-1.5 text-xs font-semibold transition-colors ${value===option.value?"bg-accent text-accent-contrast":"text-muted hover:text-text"}`}>{option.label}</button>)}</div>;
}
export function ThemeToggle({className=""}:{className?:string}){
 const pref=useSyncExternalStore(subscribeAppearance,readThemePref,()=>"system" as ThemePref);
 const style=useSyncExternalStore(subscribeAppearance,readStylePref,()=>"bento" as StylePref);
 if(style!=="bento")return null;
 return <Segmented label="Appearance" options={MODES} value={pref} onChange={setThemePref} className={className}/>;
}
export function StyleToggle({className=""}:{className?:string}){
 const style=useSyncExternalStore(subscribeAppearance,readStylePref,()=>"bento" as StylePref);
 return <Segmented label="Style" options={STYLES} value={style} onChange={setStylePref} className={className}/>;
}
export function AppearanceControls({className=""}:{className?:string}){
 return <div className={`flex flex-wrap items-center gap-2 ${className}`}><StyleToggle/><ThemeToggle/></div>;
}
