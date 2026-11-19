"use client";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useSyncExternalStore, type ReactNode } from "react";
import { DakshaBrand } from "@/components/DakshaBrand";
import { NAV_ITEMS } from "@/components/AppNav";
import { AppearanceControls, StyleToggle, useStyle } from "@/components/ThemeToggle";
import { NotificationBell } from "@/components/NotificationBell";
import { UserBadge } from "@/components/UserBadge";
import { tileClass } from "@/components/ui";
import { FakeBadge } from "@/components/CommandCentre";
import type { StyleId } from "@/lib/theme";
import { setSidebarCollapsed, sidebarSnapshot, subscribeSidebar } from "@/lib/sidebarPref";

/** Phones get the same bottom tab bar in every theme, so the thumb reaches everything. */
function BottomTabs() {
  const pathname = usePathname();
  return (
    <nav aria-label="Main (mobile)" className="fixed inset-x-0 bottom-0 z-40 flex items-stretch justify-around border-t border-line bg-surface/95 px-2 pb-[max(0.5rem,env(safe-area-inset-bottom))] pt-1.5 backdrop-blur md:hidden">
      {NAV_ITEMS.map((i) => (
        <Link key={i.href} href={i.href} aria-current={pathname === i.href ? "page" : undefined} className={`flex min-h-12 flex-1 items-center justify-center rounded-2xl px-3 text-sm font-semibold ${pathname === i.href ? "bg-accent text-accent-contrast" : "text-muted"}`}>{i.label}</Link>
      ))}
    </nav>
  );
}

function MobileBar() {
  return (
    <div className="flex items-center justify-between gap-2 md:hidden">
      <DakshaBrand compact />
      <div className="flex items-center gap-2"><StyleToggle /><NotificationBell /><UserBadge /></div>
    </div>
  );
}

/** A search box that is not wired up yet. It looks and types like a real one, does nothing, and says so. */
function TopSearch() {
  return (
    <label className="flex min-w-0 max-w-3xl flex-1 items-center gap-2 rounded-full border border-dashed border-danger/60 bg-surface-2 px-4 py-2 text-sm text-muted focus-within:border-solid focus-within:border-accent focus-within:ring-2 focus-within:ring-[color-mix(in_srgb,var(--accent)_35%,transparent)]">
      <svg aria-hidden viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><circle cx="11" cy="11" r="7" /><path d="M20 20l-3.5-3.5" /></svg>
      <input type="search" aria-label="Search everything (placeholder)" placeholder="Search messages, mail, tasks, people…" className="field-in-pill min-w-0 flex-1 appearance-none border-0 bg-transparent p-0 text-text placeholder:text-muted" onKeyDown={(e) => { if (e.key === "Enter") e.preventDefault(); }} />
      <span className="ai-mono hidden rounded-md border border-line px-1.5 py-0.5 text-xs lg:inline">⌘K</span>
      <FakeBadge />
    </label>
  );
}

/** The minimized menu: one square per destination (its first letter), the current page filled in. */
function CompactMenu() {
  const pathname = usePathname();
  return (
    <nav aria-label="Main" className="flex flex-col items-center gap-2">
      {NAV_ITEMS.map((i) => (
        <Link key={i.href} href={i.href} title={i.label} aria-label={i.label} aria-current={pathname === i.href ? "page" : undefined}
          className={`flex h-10 w-10 items-center justify-center rounded-xl text-sm font-extrabold transition-colors ${pathname === i.href ? "bg-accent text-accent-contrast" : "bg-surface-2 text-muted hover:text-text"}`}>{i.label[0]}</Link>
      ))}
    </nav>
  );
}

function CollapseButton({ collapsed }: { collapsed: boolean }) {
  return (
    <button type="button" onClick={() => setSidebarCollapsed(!collapsed)} aria-label={collapsed ? "Expand sidebar" : "Minimize sidebar"} aria-expanded={!collapsed} title={collapsed ? "Expand sidebar" : "Minimize sidebar"}
      className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full border border-line bg-surface-2 text-sm text-muted hover:text-text">{collapsed ? "»" : "«"}</button>
  );
}

const ROMAN = ["I", "II", "III", "IV", "V", "VI"];

function Shield({ className = "" }: { className?: string }) {
  return <svg aria-hidden viewBox="0 0 24 24" width="16" height="16" className={className} fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M12 3l7 3v5c0 5-3 8-7 10-4-2-7-5-7-10V6z" /><path d="M9 12l2 2 4-4" /></svg>;
}

/** Each theme has its own sidebar menu: same two destinations, but drawn the way that theme draws things. */
function SideMenu({ style }: { style: StyleId }) {
  const pathname = usePathname();
  const items = NAV_ITEMS.map((item, index) => ({ ...item, index, active: pathname === item.href }));
  const link = (i: (typeof items)[number], className: string, body: ReactNode) => (
    <Link key={i.href} href={i.href} aria-current={i.active ? "page" : undefined} className={`${className} transition-colors`}>{body}</Link>
  );
  switch (style) {
    case "sandhya": // editorial: serif labels, an inverted pill for the current page
      return <nav aria-label="Main" className="flex flex-col gap-1">{items.map((i) => link(i, `rounded-xl px-4 py-2.5 text-lg ${i.active ? "bg-text font-semibold text-canvas" : "text-muted hover:bg-surface-2 hover:text-text"}`, i.label))}</nav>;
    case "prana": // calm: a soft glowing dot marks where you are
      return <nav aria-label="Main" className="flex flex-col gap-1">{items.map((i) => link(i, `flex items-center gap-3 rounded-2xl px-3 py-2.5 text-sm ${i.active ? "bg-surface-2 font-semibold text-text" : "text-muted hover:text-text"}`,
        <><span aria-hidden className={`h-2.5 w-2.5 rounded-full ${i.active ? "bg-accent shadow-[0_0_12px_var(--accent)]" : "bg-line"}`} />{i.label}</>))}</nav>;
    case "yantra": // instrument panel: numbered, boxed, monospace
      return <nav aria-label="Main" className="flex flex-col gap-2">{items.map((i) => link(i, `ai-mono flex items-center gap-3 border-2 border-text px-3 py-2 text-xs font-medium uppercase tracking-wider ${i.active ? "bg-text text-canvas" : "text-text hover:bg-surface-2"}`,
        <><span className={i.active ? "text-accent" : "text-muted"}>{String(i.index + 1).padStart(2, "0")}</span>{i.label}</>))}</nav>;
    case "rangoli": // bold: big colour blocks
      return <nav aria-label="Main" className="flex flex-col gap-2">{items.map((i) => link(i, `rounded-2xl px-4 py-3.5 text-base font-extrabold ${i.active ? "bg-accent text-accent-contrast" : i.index % 2 ? "bg-lime text-on-tint hover:brightness-95" : "bg-pink text-on-tint hover:brightness-95"}`, i.label))}</nav>;
    case "ratna": // ink and gold: roman numerals, hairlines, a gold underline for the current page
      return <nav aria-label="Main" className="flex flex-col">{items.map((i) => link(i, `flex items-baseline gap-3 border-b border-line py-3 text-xs uppercase tracking-[0.2em] ${i.active ? "border-b-accent text-accent" : "text-muted hover:text-text"}`,
        <><span className="text-lg normal-case tracking-normal">{ROMAN[i.index]}</span>{i.label}</>))}</nav>;
    case "galaxy": // glass: stars, and a glowing gradient pill
      return <nav aria-label="Main" className="flex flex-col gap-1.5">{items.map((i) => link(i, `flex items-center gap-3 rounded-2xl px-4 py-2.5 text-sm font-semibold ${i.active ? "bg-gradient-to-r from-[color:var(--accent)] to-fuchsia-400 text-accent-contrast shadow-[0_0_22px_color-mix(in_srgb,var(--accent)_55%,transparent)]" : "text-muted hover:bg-surface-2 hover:text-text"}`,
        <><span aria-hidden>{i.active ? "✦" : "✧"}</span>{i.label}</>))}</nav>;
    case "mitra": // friendly: round badges, chunky pills
      return <nav aria-label="Main" className="flex flex-col gap-2">{items.map((i) => link(i, `flex items-center gap-3 rounded-full py-2 pl-2 pr-5 text-base font-bold ${i.active ? "bg-accent text-accent-contrast" : "bg-surface-2 text-text hover:bg-line"}`,
        <><span aria-hidden className={`flex h-9 w-9 items-center justify-center rounded-full text-sm font-extrabold ${i.active ? "bg-white/30" : "bg-lime text-on-tint"}`}>{i.label[0]}</span>{i.label}</>))}</nav>;
    case "nakshatra": // star map: stars joined by a dotted line
      return <nav aria-label="Main" className="relative flex flex-col gap-1 pl-1"><span aria-hidden className="absolute bottom-3 left-[1.15rem] top-3 border-l border-dotted border-line" />{items.map((i) => link(i, `relative flex items-center gap-3 rounded-xl px-2 py-2 text-sm ${i.active ? "font-extrabold text-text" : "text-muted hover:text-text"}`,
        <><span aria-hidden className={`relative z-10 flex h-5 w-5 items-center justify-center rounded-full bg-surface text-xs ${i.active ? "text-accent" : "text-muted"}`}>{i.active ? "✦" : "·"}</span>{i.label}</>))}</nav>;
    case "kavach": // trust: a shield mark and an outlined current page
      return <nav aria-label="Main" className="flex flex-col gap-1.5">{items.map((i) => link(i, `flex items-center gap-3 rounded-xl border px-3 py-2.5 text-sm font-semibold ${i.active ? "border-accent bg-surface-2 text-text" : "border-transparent text-muted hover:bg-surface-2 hover:text-text"}`,
        <><Shield className={i.active ? "text-accent" : "opacity-40"} />{i.label}</>))}</nav>;
    default: // Sandhya x Yantra: warm pills
      return <nav aria-label="Main" className="flex flex-col gap-1.5">{items.map((i) => link(i, `rounded-full px-4 py-2.5 text-sm font-medium ${i.active ? "bg-accent text-accent-contrast" : "text-muted hover:bg-surface-2 hover:text-text"}`, i.label))}</nav>;
  }
}

/** The line at the foot of each theme's sidebar, in that theme's voice. */
function SideFooter({ style }: { style: StyleId }) {
  switch (style) {
    case "yantra": return <p className="ai-mono border-2 border-dashed border-text px-3 py-2 text-[11px] font-medium">ADVANCED ▸</p>;
    case "hybrid": return <p className="ai-mono rounded-xl border-2 border-dashed border-text px-3 py-2 text-[11px] font-medium">ADVANCED ▸</p>;
    case "kavach": return <p className="flex items-center gap-2 text-xs font-semibold text-muted"><Shield className="text-accent" />You are in control</p>;
    case "ratna": return <p className="text-[10px] uppercase tracking-[0.24em] text-muted">Daksha · The Prajapati</p>;
    case "nakshatra": return <p className="text-xs text-muted">✦ your sky tonight</p>;
    case "galaxy": return <p className="text-xs text-muted">✧ everything in orbit</p>;
    case "mitra": return <p className="text-sm font-bold text-muted">Hi, I&apos;m here to help!</p>;
    case "prana": return <p className="text-xs text-muted">Calm, and listening.</p>;
    default: return null;
  }
}

export function AppShell({ children }: { children: ReactNode }) {
  const style = useStyle();
  const collapsed = useSyncExternalStore(subscribeSidebar, sidebarSnapshot, () => false);
  return (
    <>
      <div data-nav={style} data-sidebar={collapsed ? "collapsed" : "open"} className={`grid w-full flex-1 grid-cols-[minmax(0,1fr)] content-start gap-3 p-3 pb-24 transition-[padding] duration-300 md:gap-4 md:p-4 md:pb-4 lg:pr-[calc(var(--chat-rail-w,0rem)+1rem)] ${collapsed ? "md:grid-cols-[3.75rem_minmax(0,1fr)]" : "md:grid-cols-[12rem_minmax(0,1fr)]"}`}>
        <aside className={`hidden min-w-0 flex-col md:sticky md:top-4 md:flex md:h-[calc(100vh-2rem)] md:self-start md:overflow-y-auto ${tileClass} ${collapsed ? "items-center gap-4 px-2 py-3" : "gap-4 p-3"}`}>
          {collapsed ? (
            <>
              <DakshaBrand compact />
              <CollapseButton collapsed />
              <CompactMenu />
              <div className="mt-auto"><StyleToggle compact /></div>
            </>
          ) : (
            <>
              <div className="flex items-center justify-between gap-2"><DakshaBrand /><CollapseButton collapsed={false} /></div>
              <SideMenu style={style} />
              <div className="mt-auto flex flex-col gap-3">
                <SideFooter style={style} />
                <div><p className="mb-2 px-1 text-xs font-semibold uppercase tracking-wide text-muted">Appearance</p><AppearanceControls /></div>
              </div>
            </>
          )}
        </aside>
        <main id="main" className="flex min-w-0 flex-col gap-3 md:gap-4">
          <MobileBar />
          <header className={`hidden items-center justify-between gap-3 p-3 md:flex ${tileClass}`}>
            <TopSearch />
            <div className="flex items-center gap-3"><NotificationBell /><UserBadge /></div>
          </header>
          {children}
        </main>
      </div>
      <BottomTabs />
    </>
  );
}
