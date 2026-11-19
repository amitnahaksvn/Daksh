"use client";
import Link from "next/link";
import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from "react";
import { BrandIcon, BrandTile } from "@/components/integrations";
import { ChatAvatar } from "@/components/ChatAvatar";
import { ChatThread, chatDisplayName } from "@/components/WhatsAppChatsPanel";
import { TelegramThread } from "@/components/TelegramChatsPanel";
import { api, type TelegramChat, type WhatsAppChat } from "@/lib/api";
import { useHiddenChatConnections } from "@/lib/chatVisibility";
import { getIntegrationsHub } from "@/lib/signalr";

type Channel = "whatsapp" | "telegram";
type Account = { connectionId: string; chats: WhatsAppChat[] | TelegramChat[]; status: string; error: string | null };
type Row = { channel: Channel; connectionId: string; chatId: string; name: string; isGroup: boolean; preview: string; at: string; unread: number; pinned?: boolean; muted?: boolean };

const LABEL: Record<Channel, string> = { whatsapp: "WhatsApp", telegram: "Telegram" };
// The real brand marks (the same ones the Apps page uses).
const BRAND: Record<Channel, { icon: string; color: string }> = {
  whatsapp: { icon: "SiWhatsapp", color: "#25D366" },
  telegram: { icon: "SiTelegram", color: "#26A5E4" },
};
const MAX_WINDOWS = 3;
type ChatWindow = { channel: Channel; chatId: string; minimized: boolean };
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

/** Plain-language description of a connection that is not healthy, or null when it is fine. */
export function connectionIssue(status: string, error: string | null): string | null {
  if (status === "Connected") return null;
  if (status === "Connecting") return "Reconnecting…";
  if (status === "Error") return error ? `Problem: ${error}` : "The connection reported an error.";
  return "Not connected. New messages will not arrive.";
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
  // The panel is always there but minimized to a slim strip. It opens by itself only when there is something unread (and
  // stays open while you use it), and the person can open or minimize it by hand. Nothing is remembered between visits.
  const [userOpen, setUserOpen] = useState(false);
  const [dismissedUnread, setDismissedUnread] = useState(0);
  const [overlayOpen, setOverlayOpen] = useState(false);
  const [windows, setWindows] = useState<ChatWindow[]>([]);
  const [query, setQuery] = useState("");
  const [closedGroups, setClosedGroups] = useState<Set<Channel>>(new Set());

  // The last accounts we successfully saw. A failed or in-between refresh must never make the panel vanish.
  const accountsRef = useRef<Partial<Record<Channel, Account>>>({});
  const refresh = useCallback(async () => {
    type Probe = { ok: true; connection: { connectionId: string; status: string; lastError: string | null } | null } | { ok: false };
    const probe = (key: Channel): Promise<Probe> => api.appDetail(key).then((d): Probe => ({ ok: true, connection: d.connection }), (): Probe => ({ ok: false }));
    const [wa, tg] = await Promise.all([probe("whatsapp"), probe("telegram")]);
    const previous = accountsRef.current;
    const next: Partial<Record<Channel, Account>> = {};
    const settle = async (channel: Channel, p: Probe) => {
      if (!p.ok) { if (previous[channel]) next[channel] = previous[channel]; return; } // transient failure: keep what we had
      const c = p.connection;
      if (!c) return; // never connected: nothing to show
      // Any existing connection stays in the panel, so a problem is visible where the chats are. Chats are only fetched
      // while the connection is up; otherwise the last known ones stay (dimmed) next to the problem.
      const fallback = previous[channel]?.connectionId === c.connectionId ? previous[channel]!.chats : [];
      const up = c.status === "Connected" || c.status === "Connecting";
      const chats = !up ? fallback : channel === "whatsapp"
        ? await api.whatsAppChats(c.connectionId).catch(() => fallback)
        : await api.telegramChats(c.connectionId).catch(() => fallback);
      next[channel] = { connectionId: c.connectionId, chats, status: c.status, error: c.lastError } as Account;
    };
    await Promise.all([settle("whatsapp", wa), settle("telegram", tg)]);
    accountsRef.current = next;
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
      setUserOpen(true);
      setOverlayOpen(true);
      if (channel) setClosedGroups((g) => { const n = new Set(g); n.delete(channel); return n; });
      void refresh();
    };
    window.addEventListener(OPEN_CHAT_EVENT, onOpen);
    return () => window.removeEventListener(OPEN_CHAT_EVENT, onOpen);
  }, [refresh]);

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
          ? (account.chats as WhatsAppChat[]).filter((c) => !c.archived).map((c) => ({ channel, connectionId: account.connectionId, chatId: c.chatId, name: chatDisplayName(c), isGroup: c.isGroup, preview: c.lastMessagePreview, at: c.lastMessageAt, unread: c.unreadCount, pinned: c.pinned, muted: Boolean(c.mutedUntil && new Date(c.mutedUntil).getTime() > Date.now()) }))
          : (account.chats as TelegramChat[]).map((c) => ({ channel, connectionId: account.connectionId, chatId: c.chatId, name: c.name, isGroup: c.isGroup, preview: c.lastMessagePreview ?? "", at: c.lastMessageAt ?? "", unread: c.unreadCount }));
        const filtered = q ? rows.filter((r) => r.name.toLowerCase().includes(q) || r.preview.toLowerCase().includes(q)) : rows;
        return { channel, connectionId: account.connectionId, rows: filtered, unread: rows.reduce((n, r) => n + r.unread, 0), status: account.status, error: account.error };
      });
  }, [accounts, hidden, query]);

  const totalUnread = groups.reduce((n, g) => n + g.unread, 0);
  // What should pull the panel open: unread messages in chats that are not muted (archived chats are already left out).
  const attentionUnread = groups.reduce((n, g) => n + g.rows.reduce((m, r) => m + (r.muted ? 0 : r.unread), 0), 0);
  // If the unread count fell below what was dismissed, the next new message must open the panel again.
  if (attentionUnread < dismissedUnread) setDismissedUnread(attentionUnread);
  const collapsed = !(userOpen || attentionUnread > dismissedUnread);
  const setCollapsedPersisted = (minimize: boolean) => {
    if (minimize) { setUserOpen(false); setDismissedUnread(attentionUnread); } else setUserOpen(true);
  };
  const visible = desktop || overlayOpen;

  // Let the page make room for the fixed sidebar on desktop.
  useEffect(() => {
    const width = desktop && groups.length > 0 ? (collapsed ? "3.25rem" : "16rem") : "0rem";
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
            className={`chat-window pointer-events-auto flex ${w.channel === "whatsapp" ? "w-[min(19rem,calc(100vw-1.5rem))]" : "w-[min(16rem,calc(100vw-1.5rem))]"} flex-col overflow-hidden rounded-t-2xl border border-b-0 border-line bg-surface shadow-[0_-6px_28px_rgb(0_0_0/0.22)] ${w.minimized ? "" : "h-[min(32rem,calc(100vh-3rem))]"}`}
          >
            {(w.minimized || w.channel !== "whatsapp") && <header className="flex shrink-0 items-center gap-2 border-b border-line bg-surface-2 px-3 py-2">
              <button type="button" onClick={() => toggleMinimize(w.channel, w.chatId)} className="flex min-w-0 flex-1 items-center gap-2 text-left" aria-label={w.minimized ? `Expand chat with ${name}` : `Minimise chat with ${name}`}>
                <span className="flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-white"><BrandIcon icon={brand.icon} color={brand.color} size={14} /></span>
                <span className="truncate text-sm font-bold">{name}</span>
              </button>
              <button type="button" onClick={() => toggleMinimize(w.channel, w.chatId)} aria-label={w.minimized ? "Expand" : "Minimise"} className="rounded-full px-2 text-muted hover:bg-line hover:text-text">{w.minimized ? "▢" : "–"}</button>
              <button type="button" onClick={() => closeWindow(w.channel, w.chatId)} aria-label="Close chat" className="rounded-full px-2 text-muted hover:bg-line hover:text-text">✕</button>
            </header>}
            {!w.minimized && (
              <div className="flex min-h-0 flex-1 flex-col overflow-hidden">
                {w.channel === "whatsapp" && wa ? (
                  <ChatThread connectionId={group.connectionId} chat={wa} onClose={() => closeWindow(w.channel, w.chatId)} onMinimize={() => toggleMinimize(w.channel, w.chatId)} onSent={() => void refresh()} embedded bodyHeightClass="h-[min(14rem,calc(100vh-17rem))]" />
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
      <aside aria-label="Chats" className="chat-rail fixed bottom-0 right-0 top-0 z-30 flex w-[3.25rem] flex-col items-center gap-2.5 border-l border-line bg-surface py-3">
        <button type="button" onClick={() => setCollapsedPersisted(false)} aria-label="Expand chats" className="relative flex h-9 w-9 items-center justify-center rounded-full bg-accent text-accent-contrast">
          ‹
          {totalUnread > 0 && <span className="absolute -right-1 -top-1">{badge(totalUnread)}</span>}
        </button>
        {groups.map((g) => (
          <button key={g.channel} type="button" onClick={() => { setCollapsedPersisted(false); setClosedGroups((c) => { const n = new Set(c); n.delete(g.channel); return n; }); }} aria-label={`${LABEL[g.channel]}, ${g.unread} unread`} className="relative flex h-9 w-9 items-center justify-center rounded-full bg-surface-2 text-lg hover:bg-line">
            <BrandIcon icon={BRAND[g.channel].icon} color={BRAND[g.channel].color} size={22} />
            {g.unread > 0 && <span className="absolute -right-1 -top-1">{badge(g.unread)}</span>}
            {g.status !== "Connected" && <span aria-label="Connection problem" className="absolute -bottom-0.5 -right-0.5 h-3 w-3 rounded-full bg-danger ring-2 ring-[var(--surface)]" />}
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
    <aside aria-label="Chats" className={`chat-rail fixed bottom-0 right-0 top-0 z-30 flex w-full flex-col border-l border-line bg-surface ${desktop ? "max-w-[16rem]" : "max-w-[24rem] shadow-[-12px_0_40px_rgb(0_0_0/0.25)]"}`}>
      <header className="flex items-center justify-between gap-2 border-b border-line px-3 py-2">
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
                  {connectionIssue(g.status, g.error) && (
                    <div role="alert" className="mx-2 mb-1 flex items-center gap-2 rounded-xl bg-danger-bg px-3 py-2 text-xs text-danger">
                      <span className="min-w-0 flex-1">{connectionIssue(g.status, g.error)}</span>
                      {g.status !== "Connecting" && <Link href={`/apps/${g.channel}`} className="shrink-0 rounded-full border border-current px-2.5 py-1 font-semibold">Reconnect</Link>}
                    </div>
                  )}
                  {open && (g.rows.length === 0 ? (
                    <p className="px-3 py-2 text-xs text-muted">{query ? "No matches." : g.status === "Connected" ? "No chats yet." : "No chats to show."}</p>
                  ) : (
                    <ul className={g.status === "Connected" ? "" : "opacity-60"}>
                      {g.rows.map((r) => (
                        <li key={r.chatId}>
                          <button type="button" onClick={() => openWindow(r.channel, r.chatId)} className="flex w-full items-center gap-2 rounded-xl px-1.5 py-1.5 text-left hover:bg-surface-2">
                            <span className="relative shrink-0">
                              <ChatAvatar channel={r.channel} connectionId={r.connectionId} chatId={r.chatId} name={r.name} isGroup={r.isGroup} size={32} />
                            </span>
                            <span className="min-w-0 flex-1">
                              <span className="flex items-center gap-1.5">
                                <span className={`truncate text-sm ${r.unread > 0 ? "font-extrabold" : "font-semibold"}`}>{r.name}</span>
                                {r.isGroup && <span className="ai-mono rounded-full bg-surface-2 px-1.5 py-0.5 text-[9px] font-semibold text-muted">GROUP</span>}
                                {r.pinned && <span aria-label="Pinned" className="text-[10px]">📌</span>}
                                {r.muted && <span aria-label="Muted" className="text-[10px]">🔕</span>}
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
