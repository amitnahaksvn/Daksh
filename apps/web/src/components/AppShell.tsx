"use client";
import Link from "next/link";
import { usePathname } from "next/navigation";
import type { ReactNode } from "react";
import { DakshaBrand } from "@/components/DakshaBrand";
import { NAV_ITEMS } from "@/components/AppNav";
import { AppearanceControls, StyleToggle, useLayout, useStyle } from "@/components/ThemeToggle";
import { NotificationBell } from "@/components/NotificationBell";
import { UserBadge } from "@/components/UserBadge";
import { tileClass } from "@/components/ui";
import { effectiveShell } from "@/lib/theme";

const linkClass = (active: boolean) =>
  `whitespace-nowrap rounded-full px-4 py-2.5 text-sm font-medium transition-colors ${active ? "bg-accent text-accent-contrast" : "text-muted hover:bg-surface-2 hover:text-text"}`;

function TopLinks({ className = "" }: { className?: string }) {
  const pathname = usePathname();
  return (
    <nav aria-label="Main" className={`flex gap-1 ${className}`}>
      {NAV_ITEMS.map((i) => <Link key={i.href} href={i.href} aria-current={pathname === i.href ? "page" : undefined} className={linkClass(pathname === i.href)}>{i.label}</Link>)}
    </nav>
  );
}

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

/** Sidebar: the full-width left menu (Sandhya, Sandhya x Yantra, Kavach). */
function SidebarShell({ children }: { children: ReactNode }) {
  return (
    <div className="grid w-full flex-1 grid-cols-[minmax(0,1fr)] content-start gap-3 p-3 pb-24 md:grid-cols-[15rem_minmax(0,1fr)] md:gap-4 md:p-4 md:pb-4 lg:pr-[calc(var(--chat-rail-w,0rem)+1rem)] transition-[padding] duration-300">
      <aside className={`hidden min-w-0 flex-col gap-4 p-4 md:sticky md:top-4 md:flex md:h-[calc(100vh-2rem)] md:self-start md:overflow-y-auto ${tileClass}`}>
        <DakshaBrand />
        <TopLinks className="flex-col" />
        <div className="mt-auto"><p className="mb-2 px-1 text-xs font-semibold uppercase tracking-wide text-muted">Appearance</p><AppearanceControls /></div>
      </aside>
      <main id="main" className="flex min-w-0 flex-col gap-3 md:gap-4">
        <MobileBar />
        <header className={`hidden items-center justify-end gap-3 p-3 md:flex ${tileClass}`}><NotificationBell /><UserBadge /></header>
        {children}
      </main>
    </div>
  );
}

/** Rail: a slim icon-style column with small caps labels (Ratna, Nakshatra). */
function RailShell({ children }: { children: ReactNode }) {
  const pathname = usePathname();
  return (
    <div className="grid w-full flex-1 grid-cols-[minmax(0,1fr)] content-start gap-3 p-3 pb-24 md:grid-cols-[5.5rem_minmax(0,1fr)] md:gap-4 md:p-4 md:pb-4 lg:pr-[calc(var(--chat-rail-w,0rem)+1rem)] transition-[padding] duration-300">
      <aside className={`hidden min-w-0 flex-col items-center gap-6 px-2 py-5 md:sticky md:top-4 md:flex md:h-[calc(100vh-2rem)] md:self-start ${tileClass}`}>
        <DakshaBrand compact />
        <nav aria-label="Main" className="flex flex-col items-center gap-4">
          {NAV_ITEMS.map((i) => <Link key={i.href} href={i.href} aria-current={pathname === i.href ? "page" : undefined} className={`ai-mono text-[11px] font-bold uppercase tracking-[0.18em] ${pathname === i.href ? "text-accent" : "text-muted hover:text-text"}`}>{i.label}</Link>)}
        </nav>
        <div className="mt-auto"><StyleToggle /></div>
      </aside>
      <main id="main" className="flex min-w-0 flex-col gap-3 md:gap-4">
        <MobileBar />
        <header className="hidden items-center justify-end gap-3 md:flex"><NotificationBell /><UserBadge /></header>
        {children}
      </main>
    </div>
  );
}

/** Top: brand and a centred pill menu across the top; content is centred below (Prana, Yantra, Rangoli, Galaxy, Mitra). */
function TopShell({ children }: { children: ReactNode }) {
  return (
    <div className="flex w-full flex-1 flex-col gap-3 p-3 pb-24 md:gap-4 md:p-4 md:pb-4 lg:pr-[calc(var(--chat-rail-w,0rem)+1rem)] transition-[padding] duration-300">
      <header className="mx-auto hidden w-full max-w-6xl items-center justify-between gap-4 md:flex">
        <DakshaBrand />
        <div className={`flex items-center gap-1 rounded-full border border-line bg-surface p-1 shadow-[var(--shadow)]`}><TopLinks /></div>
        <div className="flex items-center gap-2"><StyleToggle /><NotificationBell /><UserBadge /></div>
      </header>
      <main id="main" className="mx-auto flex w-full min-w-0 max-w-6xl flex-col gap-3 md:gap-4">
        <MobileBar />
        {children}
      </main>
    </div>
  );
}

export function AppShell({ children }: { children: ReactNode }) {
  const style = useStyle();
  const layout = useLayout();
  const shell = effectiveShell(style, layout);
  const frame = shell === "rail" ? <RailShell>{children}</RailShell> : shell === "top" ? <TopShell>{children}</TopShell> : <SidebarShell>{children}</SidebarShell>;
  return <><div data-shell={shell} className="contents">{frame}</div><BottomTabs /></>;
}
