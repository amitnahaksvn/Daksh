"use client";
import { useCallback, useEffect, useMemo, useState, useSyncExternalStore } from "react";
import { BrandIcon, BrandTile } from "@/components/integrations";
import { ChatThread, chatDisplayName } from "@/components/WhatsAppChatsPanel";
import { TelegramThread } from "@/components/TelegramChatsPanel";
import { api, type TelegramChat, type WhatsAppChat } from "@/lib/api";
import { useHiddenChatConnections } from "@/lib/chatVisibility";
import { getIntegrationsHub } from "@/lib/signalr";

type Channel = "whatsapp" | "telegram";
type Account = { connectionId: string; chats: WhatsAppChat[] | TelegramChat[] };
type Row = { channel: Channel; connectionId: string; chatId: string; name: string; isGroup: boolean; preview: string; at: string; unread: number };

const LABEL: Record<Channel, string> = { whatsapp: "WhatsApp", telegram: "Telegram" };
// The real brand marks (the same ones the Apps page uses).
const BRAND: Record<Channel, { icon: string; color: string }> = {
  whatsapp: { icon: "SiWhatsapp", color: "#25D366" },
  telegram: { icon: "SiTelegram", color: "#26A5E4" },
};
const MAX_WINDOWS = 3;
type ChatWindow = { channel: Channel; chatId: string; minimized: boolean };
const COLLAPSED_KEY = "daksh.chatRail.collapsed";
export const OPEN_CHAT_EVENT = "daksh:open-chat";

/** Ask the chat sidebar to open (optionally on a given channel) from anywhere in the app. */
export function openChatDock(channel?: Channel) {
  window.dispatchEvent(new CustomEvent(OPEN_CHAT_EVENT, { detail: { channel } }));
}

function useDesktop(): boolean {
  return useSyncExternalStore(
    (cb) => {
      const mq = window.matchMedia("(min-width: 1024px)");
      mq.addEventListener("change", cb);
      return () => mq.removeEventListener("change", cb);
    },
    () => window.matchMedia("(min-width: 1024px)").matches,
    () => false,
  );
}

function shortTime(iso: string): string {
  if (!iso) return "";
  const d = new Date(iso);
  const now = new Date();
  return d.toDateString() === now.toDateString() ? d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" }) : d.toLocaleDateString([], { day: "numeric", month: "short" });
}

/** A chat sidebar fixed on the right of every page, like a messenger's contact list. As soon as any messaging
 *  service is connected, all of its chats appear here, grouped by service. Opening one shows the conversation
 *  inside the sidebar, so you never leave the page you're on. On narrow screens it becomes a button + overlay. */
export function ChatDock() {
  const desktop = useDesktop();
  const hidden = useHiddenChatConnections();
  const [accounts, setAccounts] = useState<Partial<Record<Channel, Account>>>({});
  const [collapsed, setCollapsed] = useState(() => {
    try { return window.localStorage.getItem(COLLAPSED_KEY) === "1"; } catch { return false; }
  });
  const [overlayOpen, setOverlayOpen] = useState(false);
  const [windows, setWindows] = useState<ChatWindow[]>([]);
  const [query, setQuery] = useState("");
  const [closedGroups, setClosedGroups] = useState<Set<Channel>>(new Set());

  const refresh = useCallback(async () => {
    const [wa, tg] = await Promise.all([api.appDetail("whatsapp").catch(() => null), api.appDetail("telegram").catch(() => null)]);
    const next: Partial<Record<Channel, Account>> = {};
    if (wa?.connection?.status === "Connected") {
      next.whatsapp = { connectionId: wa.connection.connectionId, chats: await api.whatsAppChats(wa.connection.connectionId).catch(() => []) };
    }
    if (tg?.connection?.status === "Connected") {
      next.telegram = { connectionId: tg.connection.connectionId, chats: await api.telegramChats(tg.connection.connectionId).catch(() => []) };
    }
    setAccounts(next);
  }, []);

  useEffect(() => {
    const first = setTimeout(() => void refresh(), 0);
    const interval = setInterval(() => void refresh(), 8000);
    let debounce: ReturnType<typeof setTimeout> | null = null;
    const onEvent = () => {
      if (debounce) clearTimeout(debounce);
      debounce = setTimeout(() => void refresh(), 500);
    };
    const hub = getIntegrationsHub();
    hub.on("WhatsAppMessageReceived", onEvent);
    hub.on("WhatsAppMessageSent", onEvent);
    hub.on("TelegramMessageReceived", onEvent);
    return () => {
      clearTimeout(first);
      clearInterval(interval);
      if (debounce) clearTimeout(debounce);
      hub.off("WhatsAppMessageReceived", onEvent);
      hub.off("WhatsAppMessageSent", onEvent);
      hub.off("TelegramMessageReceived", onEvent);
    };
  }, [refresh]);

  useEffect(() => {
    const onOpen = (e: Event) => {
      const channel = (e as CustomEvent<{ channel?: Channel }>).detail?.channel;
      setCollapsed(false);
      setOverlayOpen(true);
      if (channel) setClosedGroups((g) => { const n = new Set(g); n.delete(channel); return n; });
      void refresh();
    };
    window.addEventListener(OPEN_CHAT_EVENT, onOpen);
    return () => window.removeEventListener(OPEN_CHAT_EVENT, onOpen);
  }, [refresh]);

  function setCollapsedPersisted(value: boolean) {
    setCollapsed(value);
    try { window.localStorage.setItem(COLLAPSED_KEY, value ? "1" : "0"); } catch { /* storage unavailable */ }
  }

  /** Opens a conversation as its own window next to the sidebar (or brings the existing one forward). */
  function openWindow(channel: Channel, chatId: string) {
    setWindows((w) => {
      const rest = w.filter((x) => !(x.channel === channel && x.chatId === chatId));
      return [{ channel, chatId, minimized: false }, ...rest].slice(0, MAX_WINDOWS);
    });
    if (!desktop) setOverlayOpen(false);
  }
  const closeWindow = (channel: Channel, chatId: string) => setWindows((w) => w.filter((x) => !(x.channel === channel && x.chatId === chatId)));
  const toggleMinimize = (channel: Channel, chatId: string) =>
    setWindows((w) => w.map((x) => (x.channel === channel && x.chatId === chatId ? { ...x, minimized: !x.minimized } : x)));

  const groups = useMemo(() => {
    const q = query.trim().toLowerCase();
    return (Object.keys(accounts) as Channel[])
      .filter((c) => accounts[c] && !hidden.has(accounts[c]!.connectionId))
      .map((channel) => {
        const account = accounts[channel]!;
        const rows: Row[] = channel === "whatsapp"
          ? (account.chats as WhatsAppChat[]).map((c) => ({ channel, connectionId: account.connectionId, chatId: c.chatId, name: chatDisplayName(c), isGroup: c.isGroup, preview: c.lastMessagePreview, at: c.lastMessageAt, unread: c.unreadCount }))
          : (account.chats as TelegramChat[]).map((c) => ({ channel, connectionId: account.connectionId, chatId: c.chatId, name: c.name, isGroup: c.isGroup, preview: c.lastMessagePreview ?? "", at: c.lastMessageAt ?? "", unread: c.unreadCount }));
        const filtered = q ? rows.filter((r) => r.name.toLowerCase().includes(q) || r.preview.toLowerCase().includes(q)) : rows;
        return { channel, connectionId: account.connectionId, rows: filtered, unread: rows.reduce((n, r) => n + r.unread, 0) };
      });
  }, [accounts, hidden, query]);

  const totalUnread = groups.reduce((n, g) => n + g.unread, 0);
  const visible = desktop || overlayOpen;

  // Let the page make room for the fixed sidebar on desktop.
  useEffect(() => {
    const width = desktop && groups.length > 0 ? (collapsed ? "4.25rem" : "21rem") : "0rem";
    document.documentElement.style.setProperty("--chat-rail-w", width);
    return () => {
      document.documentElement.style.removeProperty("--chat-rail-w");
    };
  }, [desktop, collapsed, groups.length]);

  useEffect(() => {
    if (desktop || !overlayOpen) return;
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && setOverlayOpen(false);
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [desktop, overlayOpen]);

  if (groups.length === 0) return null;

  const badge = (n: number) => n > 0 && (
    <span key={n} className="badge-pop flex h-5 min-w-5 items-center justify-center rounded-full bg-pink px-1.5 text-[10px] font-bold text-on-tint">{n > 99 ? "99+" : n}</span>
  );

  const windowsLayer = windows.length > 0 && (
    <div
      className="pointer-events-none fixed bottom-0 z-30 flex flex-row-reverse items-end gap-3 px-3"
      style={{ right: desktop ? "calc(var(--chat-rail-w, 0rem) + 0.5rem)" : "0" }}
    >
      {windows.map((w) => {
        const group = groups.find((g) => g.channel === w.channel);
        if (!group) return null;
        const wa = w.channel === "whatsapp" ? (accounts.whatsapp?.chats as WhatsAppChat[] | undefined)?.find((c) => c.chatId === w.chatId) : undefined;
        const tg = w.channel === "telegram" ? (accounts.telegram?.chats as TelegramChat[] | undefined)?.find((c) => c.chatId === w.chatId) : undefined;
        const name = wa ? chatDisplayName(wa) : tg?.name ?? "Chat";
        const brand = BRAND[w.channel];
        return (
          <section
            key={`${w.channel}:${w.chatId}`}
            aria-label={`Chat with ${name}`}
            className={`chat-window pointer-events-auto flex w-[min(20.5rem,calc(100vw-1.5rem))] flex-col overflow-hidden rounded-t-2xl border border-b-0 border-line bg-surface shadow-[0_-6px_28px_rgb(0_0_0/0.22)] ${w.minimized ? "" : "h-[min(30rem,calc(100vh-5rem))]"}`}
          >
            <header className="flex shrink-0 items-center gap-2 border-b border-line bg-surface-2 px-3 py-2">
              <button type="button" onClick={() => toggleMinimize(w.channel, w.chatId)} className="flex min-w-0 flex-1 items-center gap-2 text-left" aria-label={w.minimized ? `Expand chat with ${name}` : `Minimise chat with ${name}`}>
                <span className="flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-white"><BrandIcon icon={brand.icon} color={brand.color} size={14} /></span>
                <span className="truncate text-sm font-bold">{name}</span>
              </button>
              <button type="button" onClick={() => toggleMinimize(w.channel, w.chatId)} aria-label={w.minimized ? "Expand" : "Minimise"} className="rounded-full px-2 text-muted hover:bg-line hover:text-text">{w.minimized ? "▢" : "–"}</button>
              <button type="button" onClick={() => closeWindow(w.channel, w.chatId)} aria-label="Close chat" className="rounded-full px-2 text-muted hover:bg-line hover:text-text">✕</button>
            </header>
            {!w.minimized && (
              <div className="min-h-0 flex-1 overflow-hidden">
                {w.channel === "whatsapp" && wa ? (
                  <ChatThread connectionId={group.connectionId} chat={wa} onClose={() => closeWindow(w.channel, w.chatId)} onSent={() => void refresh()} embedded bodyHeightClass="h-[min(22.5rem,calc(100vh-14rem))]" />
                ) : tg ? (
                  <TelegramThread connectionId={group.connectionId} chat={tg} onBack={() => closeWindow(w.channel, w.chatId)} embedded heightClass="h-full" />
                ) : (
                  <p className="p-4 text-sm text-muted">Loading…</p>
                )}
              </div>
            )}
          </section>
        );
      })}
    </div>
  );

  // Desktop, collapsed: a slim strip with an unread badge per service.
  if (desktop && collapsed) {
    return (
      <>
      {windowsLayer}
      <aside aria-label="Chats" className="chat-rail fixed bottom-0 right-0 top-0 z-30 flex w-[4.25rem] flex-col items-center gap-3 border-l border-line bg-surface py-4">
        <button type="button" onClick={() => setCollapsedPersisted(false)} aria-label="Expand chats" className="relative flex h-10 w-10 items-center justify-center rounded-full bg-accent text-accent-contrast">
          ‹
          {totalUnread > 0 && <span className="absolute -right-1 -top-1">{badge(totalUnread)}</span>}
        </button>
        {groups.map((g) => (
          <button key={g.channel} type="button" onClick={() => { setCollapsedPersisted(false); setClosedGroups((c) => { const n = new Set(c); n.delete(g.channel); return n; }); }} aria-label={`${LABEL[g.channel]}, ${g.unread} unread`} className="relative flex h-10 w-10 items-center justify-center rounded-full bg-surface-2 text-lg hover:bg-line">
            <BrandIcon icon={BRAND[g.channel].icon} color={BRAND[g.channel].color} size={22} />
            {g.unread > 0 && <span className="absolute -right-1 -top-1">{badge(g.unread)}</span>}
          </button>
        ))}
      </aside>
      </>
    );
  }

  if (!visible) {
    return (
      <>
      {windowsLayer}
      <button type="button" onClick={() => setOverlayOpen(true)} aria-label={totalUnread > 0 ? `Open chats, ${totalUnread} unread` : "Open chats"} className="fixed bottom-5 right-5 z-40 flex h-14 w-14 items-center justify-center rounded-full bg-accent text-accent-contrast shadow-[0_10px_28px_rgb(0_0_0/0.3)] transition-transform hover:scale-105 active:scale-95">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" className="h-6 w-6"><path d="M4 5.5A2.5 2.5 0 0 1 6.5 3h11A2.5 2.5 0 0 1 20 5.5v8a2.5 2.5 0 0 1-2.5 2.5H10l-4.5 4v-4A2.5 2.5 0 0 1 4 13.5v-8Z" /></svg>
        {totalUnread > 0 && <span className="absolute -right-1 -top-1">{badge(totalUnread)}</span>}
      </button>
      </>
    );
  }

  return (
    <>
    {windowsLayer}
    <aside aria-label="Chats" className={`chat-rail fixed bottom-0 right-0 top-0 z-30 flex w-full flex-col border-l border-line bg-surface ${desktop ? "max-w-[21rem]" : "max-w-[24rem] shadow-[-12px_0_40px_rgb(0_0_0/0.25)]"}`}>
      <header className="flex items-center justify-between gap-2 border-b border-line px-4 py-3">
        <div className="flex items-center gap-2">
          <h2 className="text-base font-extrabold">Chats</h2>
          {badge(totalUnread)}
        </div>
        <button type="button" onClick={() => (desktop ? setCollapsedPersisted(true) : setOverlayOpen(false))} aria-label={desktop ? "Collapse chats" : "Close chats"} className="rounded-full px-2 py-1 text-muted hover:bg-surface-2 hover:text-text">
          {desktop ? "›" : "✕"}
        </button>
      </header>

      {(
        <>
          <div className="p-3 pb-2">
            <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search chats…" className="w-full rounded-full border border-line bg-surface-2 px-4 py-2 text-sm text-text placeholder:text-muted" />
          </div>
          <div className="min-h-0 flex-1 overflow-y-auto px-2 pb-4">
            {groups.map((g) => {
              const open = !closedGroups.has(g.channel);
              return (
                <section key={g.channel} className="mb-2">
                  <button
                    type="button"
                    aria-expanded={open}
                    onClick={() => setClosedGroups((c) => { const n = new Set(c); if (n.has(g.channel)) n.delete(g.channel); else n.add(g.channel); return n; })}
                    className="flex w-full items-center gap-2 rounded-xl px-2 py-2 text-left text-xs font-bold uppercase tracking-wide text-muted hover:bg-surface-2"
                  >
                    <span aria-hidden className={`transition-transform ${open ? "rotate-90" : ""}`}>›</span>
                    <BrandTile icon={BRAND[g.channel].icon} color={BRAND[g.channel].color} size={24} />
                    <span className="flex-1">{LABEL[g.channel]}</span>
                    {badge(g.unread)}
                  </button>
                  {open && (g.rows.length === 0 ? (
                    <p className="px-3 py-2 text-xs text-muted">{query ? "No matches." : "No chats yet."}</p>
                  ) : (
                    <ul>
                      {g.rows.map((r) => (
                        <li key={r.chatId}>
                          <button type="button" onClick={() => openWindow(r.channel, r.chatId)} className="flex w-full items-center gap-3 rounded-xl px-2 py-2 text-left hover:bg-surface-2">
                            <span className="relative shrink-0">
                              <span className="flex h-10 w-10 items-center justify-center rounded-full bg-lime text-sm font-bold text-on-tint">{r.name.slice(0, 1).toUpperCase()}</span>
                              <span className="absolute -bottom-0.5 -right-0.5 flex h-[18px] w-[18px] items-center justify-center rounded-full bg-white ring-2 ring-[var(--surface)]">
                                <BrandIcon icon={BRAND[r.channel].icon} color={BRAND[r.channel].color} size={11} />
                              </span>
                            </span>
                            <span className="min-w-0 flex-1">
                              <span className="flex items-center gap-1.5">
                                <span className={`truncate text-sm ${r.unread > 0 ? "font-extrabold" : "font-semibold"}`}>{r.name}</span>
                                {r.isGroup && <span className="ai-mono rounded-full bg-surface-2 px-1.5 py-0.5 text-[9px] font-semibold text-muted">GROUP</span>}
                              </span>
                              <span className={`block truncate text-xs ${r.unread > 0 ? "text-text" : "text-muted"}`}>{r.preview}</span>
                            </span>
                            <span className="flex shrink-0 flex-col items-end gap-1">
                              <span className="text-[10px] text-muted">{shortTime(r.at)}</span>
                              {badge(r.unread)}
                            </span>
                          </button>
                        </li>
                      ))}
                    </ul>
                  ))}
                </section>
              );
            })}
          </div>
        </>
      )}
    </aside>
    </>
  );
}
