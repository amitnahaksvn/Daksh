"use client";
import { useSyncExternalStore } from "react";
import { readAccentPref, readStylePref, readThemePref, setAccentPref, setStylePref, setThemePref, subscribeAppearance, type AccentPref, type StylePref, type ThemePref } from "@/lib/theme";
const MODES:{value:ThemePref;label:string}[]=[{value:"light",label:"Day"},{value:"dark",label:"Night"},{value:"system",label:"Auto"}];
const STYLES:{value:StylePref;label:string}[]=[{value:"bento",label:"Bento"},{value:"neural",label:"Neural"},{value:"aurora",label:"Aurora"}];
const ACCENTS:{value:AccentPref;label:string;color:string}[]=[
 {value:"violet",label:"Violet",color:"#8B5CF6"},{value:"blue",label:"Ocean",color:"#3B82F6"},
 {value:"emerald",label:"Emerald",color:"#10B981"},{value:"rose",label:"Rose",color:"#F43F5E"},
 {value:"amber",label:"Amber",color:"#F59E0B"},
];
function Segmented<T extends string>({label,options,value,onChange,className=""}:{label:string;options:{value:T;label:string}[];value:T;onChange:(v:T)=>void;className?:string}){
 return <div role="radiogroup" aria-label={label} className={`inline-flex rounded-full bg-surface-2 p-1 ${className}`}>{options.map(option=><button key={option.value} type="button" role="radio" aria-checked={value===option.value} onClick={()=>onChange(option.value)} className={`rounded-full px-3.5 py-1.5 text-xs font-semibold transition-colors ${value===option.value?"bg-accent text-accent-contrast":"text-muted hover:text-text"}`}>{option.label}</button>)}</div>;
}
function AccentPicker(){
 const accent=useSyncExternalStore(subscribeAppearance,readAccentPref,()=>"violet" as AccentPref);
 return <div className="flex flex-wrap items-center gap-2" role="radiogroup" aria-label="Accent colour">
  {ACCENTS.map(item=><button key={item.value} type="button" role="radio" aria-checked={accent===item.value} aria-label={item.label} title={item.label} onClick={()=>setAccentPref(item.value)} className={`flex h-8 items-center gap-2 rounded-full border px-2.5 text-xs font-semibold ${accent===item.value?"border-text bg-surface-2":"border-line"}`}>
   <span className="h-3.5 w-3.5 rounded-full" style={{background:item.color}}/><span className="text-text">{item.label}</span>
  </button>)}
 </div>;
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
 return <div className={`flex flex-col items-start gap-2 ${className}`}><StyleToggle/><ThemeToggle/><AccentPicker/></div>;
}
