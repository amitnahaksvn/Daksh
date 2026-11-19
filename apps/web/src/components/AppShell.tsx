"use client";
import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect, useState, useSyncExternalStore, type ReactNode } from "react";
import { DakshaBrand } from "@/components/DakshaBrand";
import { NAV_ITEMS } from "@/components/AppNav";
import { AppearanceControls, StyleToggle, useStyle } from "@/components/ThemeToggle";
import { openChatDock } from "@/components/ChatDock";
import { api, type ConversationSummary } from "@/lib/api";
import { NotificationBell } from "@/components/NotificationBell";
import { UserBadge } from "@/components/UserBadge";
import { BusyChip } from "@/components/BusyControls";
import { tileClass } from "@/components/ui";
import { FakeBadge } from "@/components/CommandCentre";
import { setSidebarCollapsed, sidebarSnapshot, subscribeSidebar } from "@/lib/sidebarPref";
import { CHATS_CHANGED_EVENT, openChat } from "@/lib/dashboardTabsState";

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
    <div className="sticky top-0 z-30 -mx-3 -mt-3 flex items-center justify-between gap-2 bg-canvas/95 px-3 py-2 backdrop-blur md:hidden">
      <DakshaBrand compact />
      <div className="flex items-center gap-2"><StyleToggle /><BusyChip /><NotificationBell /><UserBadge /></div>
    </div>
  );
}

/** A search box that is not wired up yet. It looks and types like a real one, does nothing, and says so. */
export function TopSearch() {
  return (
    <label className="flex min-w-0 max-w-3xl flex-1 items-center gap-2 rounded-full border border-dashed border-danger/60 bg-surface-2 px-4 py-2 text-sm text-muted">
      <svg aria-hidden viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><circle cx="11" cy="11" r="7" /><path d="M20 20l-3.5-3.5" /></svg>
      <input id="top-search" type="search" aria-label="Search everything (placeholder)" placeholder="Search messages, mail, tasks, people…" className="field-in-pill min-w-0 flex-1 appearance-none border-0 bg-transparent p-0 text-text placeholder:text-muted" onKeyDown={(e) => { if (e.key === "Enter") e.preventDefault(); }} />
      <span className="ai-mono hidden rounded-md border border-line px-1.5 py-0.5 text-xs lg:inline">⌘K</span>
      <FakeBadge />
    </label>
  );
}

// ── The sidebar: laid out like a chat app's. Logo with search and collapse icons on top, icon rows, then a list. ─────────
const icon = (children: ReactNode) => (
  <svg aria-hidden viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" className="shrink-0">{children}</svg>
);
const Icons = {
  compose: icon(<><path d="M12 4H7a3 3 0 0 0-3 3v10a3 3 0 0 0 3 3h10a3 3 0 0 0 3-3v-5" /><path d="M18.4 3.6a1.8 1.8 0 0 1 2.5 2.5L12 15l-3.5 1 1-3.5z" /></>),
  home: icon(<><path d="M4 11l8-7 8 7" /><path d="M6 10v9a1 1 0 0 0 1 1h3v-5h4v5h3a1 1 0 0 0 1-1v-9" /></>),
  apps: icon(<><circle cx="8" cy="8" r="3" /><circle cx="16" cy="8" r="3" /><circle cx="8" cy="16" r="3" /><circle cx="16" cy="16" r="3" /></>),
  chats: icon(<path d="M4 6.5A2.5 2.5 0 0 1 6.5 4h11A2.5 2.5 0 0 1 20 6.5v8a2.5 2.5 0 0 1-2.5 2.5H10l-4.5 4v-4A2.5 2.5 0 0 1 4 14.5z" />),
  panel: icon(<><rect x="3.5" y="4.5" width="17" height="15" rx="3" /><path d="M9.5 4.5v15" /></>),
  board: icon(<><rect x="4" y="4" width="7" height="7" rx="1.5" /><rect x="13" y="4" width="7" height="4" rx="1.5" /><rect x="13" y="10" width="7" height="10" rx="1.5" /><rect x="4" y="13" width="7" height="7" rx="1.5" /></>),
};

const rowBase = "group flex w-full items-center gap-3 rounded-xl px-2.5 py-2 text-sm font-medium transition-colors";
const rowIdle = "text-text hover:bg-surface-2";
const rowActive = "bg-surface-2 text-text";

function SideRow({ icon: i, label, collapsed, active, onClick, href }: { icon: ReactNode; label: string; collapsed: boolean; active?: boolean; onClick?: () => void; href?: string }) {
  const cls = `${rowBase} ${active ? rowActive : rowIdle} ${collapsed ? "justify-center px-0" : ""}`;
  const body = <>{i}{!collapsed && <span className="min-w-0 flex-1 truncate text-left">{label}</span>}</>;
  return href
    ? <Link href={href} aria-label={collapsed ? label : undefined} title={collapsed ? label : undefined} aria-current={active ? "page" : undefined} className={cls}>{body}</Link>
    : <button type="button" onClick={onClick} aria-label={collapsed ? label : undefined} title={collapsed ? label : undefined} className={cls}>{body}</button>;
}

/** Past Ask chats, newest first, titled by what they began with — like ChatGPT's history. */
function ChatHistory() {
  const router = useRouter();
  const pathname = usePathname();
  const [chats, setChats] = useState<ConversationSummary[]>([]);
  useEffect(() => {
    let live = true;
    const load = () => { void api.conversations().then((c) => { if (live) setChats(c); }, () => undefined); };
    load();
    window.addEventListener(CHATS_CHANGED_EVENT, load);
    return () => { live = false; window.removeEventListener(CHATS_CHANGED_EVENT, load); };
  }, []);
  async function remove(c: ConversationSummary) {
    if (!window.confirm("Delete this chat?")) return;
    try { await api.deleteConversation(c.id); setChats((all) => all.filter((x) => x.id !== c.id)); openChat(null); } catch { /* leave it listed */ }
  }
  if (chats.length === 0) return null;
  return (
    <div className="min-h-0 flex-1 overflow-y-auto">
      <p className="px-2.5 pb-1 pt-3 text-xs font-semibold text-muted">Chats</p>
      <ul className="flex flex-col">
        {chats.map((c) => (
          <li key={c.id} className="group relative">
            <button type="button" onClick={() => { openChat(c.id); if (pathname !== "/dashboard") router.push("/dashboard"); }} title={c.title ?? "Chat"} className="block w-full truncate rounded-xl px-2.5 py-2 pr-8 text-left text-sm text-text hover:bg-surface-2">{c.title || "New chat"}</button>
            <button type="button" onClick={() => void remove(c)} aria-label={`Delete chat: ${c.title ?? "chat"}`} className="absolute right-1 top-1/2 hidden -translate-y-1/2 rounded-lg px-1.5 py-1 text-xs text-muted hover:bg-surface hover:text-danger group-hover:block focus:block">✕</button>
          </li>
        ))}
      </ul>
    </div>
  );
}

function SideMenu({ collapsed }: { collapsed: boolean }) {
  const pathname = usePathname();
  const router = useRouter();
  return (
    <nav aria-label="Main" className="flex flex-col gap-0.5">
      <SideRow icon={Icons.compose} label="New chat" collapsed={collapsed} onClick={() => { openChat(null); if (pathname !== "/dashboard") router.push("/dashboard"); }} />
      <SideRow icon={Icons.home} label="Dashboard" collapsed={collapsed} href="/dashboard" active={pathname === "/dashboard"} />
      <SideRow icon={Icons.apps} label="Apps" collapsed={collapsed} href="/apps" active={pathname.startsWith("/apps")} />
      <SideRow icon={Icons.chats} label="Chats" collapsed={collapsed} onClick={() => openChatDock()} />
    </nav>
  );
}

/** The sidebar's top: the logo and the collapse icon. Collapsed, the logo sits above the collapse icon. */
function SideHeader({ collapsed }: { collapsed: boolean }) {
  const iconBtn = "flex h-9 w-9 items-center justify-center rounded-xl text-muted transition-colors hover:bg-surface-2 hover:text-text";
  const toggle = (
    <button type="button" onClick={() => setSidebarCollapsed(!collapsed)} aria-label={collapsed ? "Expand sidebar" : "Minimize sidebar"} aria-expanded={!collapsed} title={collapsed ? "Expand sidebar" : "Minimize sidebar"} className={iconBtn}>{Icons.panel}</button>
  );
  if (collapsed) return <div className="flex flex-col items-center gap-2"><DakshaBrand compact />{toggle}</div>;
  return (
    <div className="flex items-center justify-between gap-1">
      <DakshaBrand />
      <div className="flex items-center">
        {toggle}
      </div>
    </div>
  );
}

export function AppShell({ children }: { children: ReactNode }) {
  const style = useStyle();
  const collapsed = useSyncExternalStore(subscribeSidebar, sidebarSnapshot, () => false);
  return (
    <>
      <div data-nav={style} data-sidebar={collapsed ? "collapsed" : "open"} className={`grid w-full flex-1 grid-cols-[minmax(0,1fr)] content-start gap-3 p-3 pb-24 transition-[padding,grid-template-columns] duration-300 ease-out md:gap-4 md:p-4 md:pb-4 lg:pr-[calc(var(--chat-rail-w,0rem)+1rem)] ${collapsed ? "md:grid-cols-[3.75rem_minmax(0,1fr)]" : "md:grid-cols-[13rem_minmax(0,1fr)]"}`}>
        <aside className={`hidden min-w-0 flex-col gap-3 overflow-hidden md:sticky md:top-4 md:flex md:h-[calc(100vh-2rem)] md:self-start ${tileClass} ${collapsed ? "items-stretch p-2" : "p-3"}`}>
          <div key={collapsed ? "collapsed" : "open"} className="sidebar-fade flex min-h-0 flex-1 flex-col gap-3">
            <SideHeader collapsed={collapsed} />
            <SideMenu collapsed={collapsed} />
            {!collapsed && <ChatHistory />}
            <div className={`mt-auto flex flex-col gap-2 ${collapsed ? "items-center" : ""}`}>
              {collapsed ? <StyleToggle compact /> : <AppearanceControls />}
            </div>
          </div>
        </aside>
        <main id="main" className="flex min-w-0 flex-col gap-3 md:gap-4">
          <MobileBar />
          <header className={`z-30 hidden items-center justify-between gap-3 p-3 md:sticky md:top-4 md:flex ${tileClass}`}>
            {/* The search box is hidden for now (TopSearch is kept below). The dashboard page puts its Ask / dashboard tabs here. */}
            <div id="topbar-slot" className="min-w-0 flex-1" />
            <div className="flex items-center gap-3"><BusyChip /><NotificationBell /><UserBadge /></div>
          </header>
          {children}
        </main>
      </div>
      <BottomTabs />
    </>
  );
}
