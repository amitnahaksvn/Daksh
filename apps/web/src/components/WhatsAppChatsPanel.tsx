"use client";
import { useCallback, useEffect, useState } from "react";
import { ChatAvatar } from "@/components/ChatAvatar";
import { ChatThread, GroupBadge, chatDisplayName } from "@/components/WhatsAppThread";
import { buttonClass, inputClass, secondaryButtonClass } from "@/components/ui";
import { api, ApiError, type WhatsAppChat, type WhatsAppSearchHit } from "@/lib/api";
import { getIntegrationsHub } from "@/lib/signalr";

// The thread, avatar helpers and name rules live in WhatsAppThread.tsx; re-exported so existing imports keep working.
export { ChatThread, GroupBadge, chatDisplayName };

export function NewMessageForm({ connectionId, onSent }: { connectionId: string; onSent: () => void }) {
  const [open, setOpen] = useState(false);
  const [to, setTo] = useState("");
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function send() {
    setError(null);
    setBusy(true);
    try {
      await api.sendNewWhatsAppMessage(connectionId, to, text);
      setTo("");
      setText("");
      setOpen(false);
      onSent();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not send the message.");
    } finally {
      setBusy(false);
    }
  }

  if (!open) return <button onClick={() => setOpen(true)} className={`${secondaryButtonClass} px-5 py-2.5`}>New message</button>;
  return (
    <div className="rounded-[1.25rem] border border-line p-4">
      <p className="text-sm font-semibold">New message</p>
      <div className="mt-2 flex flex-col gap-2">
        <input value={to} onChange={(e) => setTo(e.target.value)} placeholder="Phone number with country code, e.g. 919876543210" className={inputClass} />
        <textarea value={text} onChange={(e) => setText(e.target.value)} placeholder="Message" rows={3} className={inputClass} />
        {error && <p role="alert" className="text-sm text-danger">{error}</p>}
        <div className="flex gap-2">
          <button onClick={send} disabled={busy || !to.trim() || !text.trim()} className={`${buttonClass} px-5 py-2`}>{busy ? "Sending…" : "Send"}</button>
          <button onClick={() => setOpen(false)} className={`${secondaryButtonClass} px-5 py-2`}>Cancel</button>
        </div>
      </div>
    </div>
  );
}

const isMuted = (c: WhatsAppChat) => Boolean(c.mutedUntil && new Date(c.mutedUntil).getTime() > Date.now());

function ChatRow({ c, connectionId, onOpen, onChanged }: { c: WhatsAppChat; connectionId: string; onOpen: () => void; onChanged: () => void }) {
  const [menu, setMenu] = useState(false);
  const muted = isMuted(c);
  async function pref(p: { pinned?: boolean; archived?: boolean; muteHours?: number }) {
    setMenu(false);
    try { await api.setWhatsAppChatPrefs(connectionId, c.chatId, p); } finally { onChanged(); }
  }
  const item = "block w-full rounded-lg px-3 py-1.5 text-left text-xs hover:bg-surface-2";
  return (
    <li className="relative">
      <div className="flex items-center gap-1 rounded-xl bg-surface-2 hover:bg-surface">
        <button onClick={onOpen} className="flex min-w-0 flex-1 items-center gap-3 p-3 text-left text-sm">
          <ChatAvatar connectionId={connectionId} chatId={c.chatId} name={chatDisplayName(c)} isGroup={c.isGroup} size={44} />
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2">
              <span className="truncate font-semibold">{chatDisplayName(c)}</span>
              {c.isGroup && <GroupBadge />}
              {c.pinned && <span aria-label="Pinned" title="Pinned" className="text-xs">📌</span>}
              {muted && <span aria-label="Muted" title="Muted" className="text-xs">🔕</span>}
            </div>
            <p className="mt-0.5 truncate text-muted">{c.lastMessagePreview}</p>
          </div>
          <div className="flex shrink-0 flex-col items-end gap-1">
            <span className="text-xs text-muted">{new Date(c.lastMessageAt).toLocaleString([], { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" })}</span>
            {c.unreadCount > 0 && <span className={`flex h-5 min-w-[1.25rem] items-center justify-center rounded-full px-1.5 text-[10px] font-bold ${muted ? "bg-surface text-muted" : "bg-pink text-on-tint"}`}>{c.unreadCount > 9 ? "9+" : c.unreadCount}</span>}
          </div>
        </button>
        <button type="button" onClick={() => setMenu((v) => !v)} aria-label={`Options for ${chatDisplayName(c)}`} aria-expanded={menu} className="mr-2 rounded-full px-2 py-1 text-muted hover:bg-surface hover:text-text">⋯</button>
      </div>
      {menu && (
        <>
          <button type="button" aria-label="Close menu" onClick={() => setMenu(false)} className="fixed inset-0 z-10 cursor-default" />
          <div role="menu" className="absolute right-2 top-full z-20 mt-1 w-44 rounded-xl border border-line bg-surface p-1 shadow-[var(--shadow)]">
            <button role="menuitem" className={item} onClick={() => void pref({ pinned: !c.pinned })}>{c.pinned ? "Unpin" : "Pin to top"}</button>
            <button role="menuitem" className={item} onClick={() => void pref({ archived: !c.archived })}>{c.archived ? "Unarchive" : "Archive"}</button>
            {muted ? <button role="menuitem" className={item} onClick={() => void pref({ muteHours: 0 })}>Unmute</button> : <button role="menuitem" className={item} onClick={() => void pref({ muteHours: 8 })}>Mute for 8 hours</button>}
            {!muted && <button role="menuitem" className={item} onClick={() => void pref({ muteHours: 24 * 7 })}>Mute for a week</button>}
          </div>
        </>
      )}
    </li>
  );
}

/** The chat list + thread view for one WhatsApp connection — the "see every message and reply right here"
 *  surface, shared between the Manage panel and the bigger Dashboard card so they stay identical. */
export function WhatsAppChatsPanel({ connectionId, heightClass = "h-[calc(100vh-22rem)] min-h-[24rem]" }: { connectionId: string; heightClass?: string }) {
  const [chats, setChats] = useState<WhatsAppChat[] | null>(null);
  const [selectedChatId, setSelectedChatId] = useState<string | null>(null);
  const [showArchived, setShowArchived] = useState(false);
  const [query, setQuery] = useState("");
  const [hits, setHits] = useState<WhatsAppSearchHit[] | null>(null);
  const [searchError, setSearchError] = useState<string | null>(null);

  const loadChats = useCallback(async () => {
    try { setChats(await api.whatsAppChats(connectionId)); } catch { /* best-effort; the panel still works without the chat list */ }
  }, [connectionId]);

  useEffect(() => { const timer = setTimeout(() => void loadChats(), 0); return () => clearTimeout(timer); }, [loadChats]);

  // Push-plus-poll fallback, same reasoning as ChatThread: SignalR should make this instant, but a dead
  // connection has repeatedly left the chat list stale until a manual refresh.
  useEffect(() => { const interval = setInterval(() => void loadChats(), 5000); return () => clearInterval(interval); }, [loadChats]);

  useEffect(() => {
    const hub = getIntegrationsHub();
    const onMessage = (payload: { connectionId: string }) => { if (payload.connectionId === connectionId) void loadChats(); };
    hub.on("WhatsAppMessageReceived", onMessage);
    hub.on("WhatsAppMessageSent", onMessage);
    hub.on("WhatsAppMessageUpdated", onMessage);
    if (hub.state === "Disconnected") hub.start().catch(() => void 0);
    return () => { hub.off("WhatsAppMessageReceived", onMessage); hub.off("WhatsAppMessageSent", onMessage); hub.off("WhatsAppMessageUpdated", onMessage); };
  }, [connectionId, loadChats]);

  // Searching every message of every chat (two letters or more), a moment after typing stops.
  useEffect(() => {
    const q = query.trim();
    if (q.length < 2) { const t = setTimeout(() => { setHits(null); setSearchError(null); }, 0); return () => clearTimeout(t); }
    const timer = setTimeout(() => {
      api.searchWhatsApp(connectionId, q).then((h) => { setHits(h); setSearchError(null); }, (e) => setSearchError(e instanceof ApiError ? e.message : "Search failed."));
    }, 300);
    return () => clearTimeout(timer);
  }, [query, connectionId]);

  const selectedChat = chats?.find((c) => c.chatId === selectedChatId) ?? null;
  if (selectedChat) return <ChatThread connectionId={connectionId} chat={selectedChat} onClose={() => setSelectedChatId(null)} onSent={loadChats} bodyHeightClass={heightClass} />;

  if (!chats) return <p className="text-sm text-muted">Loading…</p>;
  if (chats.length === 0) return <p className="text-sm text-muted">No chats yet. They&rsquo;ll show up here as messages arrive.</p>;

  const archivedCount = chats.filter((c) => c.archived).length;
  const shown = chats.filter((c) => Boolean(c.archived) === showArchived);
  const nameOf = (id: string) => { const c = chats.find((x) => x.chatId === id); return c ? chatDisplayName(c) : "Chat"; };

  return (
    <div className="space-y-2">
      <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search all messages…" aria-label="Search all messages" className={inputClass} />
      {searchError && <p role="alert" className="text-sm text-danger">{searchError}</p>}
      {hits ? (
        <ul className={`${heightClass} space-y-1 overflow-y-auto`} aria-label="Search results">
          {hits.length === 0 ? <li className="p-3 text-sm text-muted">No messages match &ldquo;{query.trim()}&rdquo;.</li> : hits.map((h) => (
            <li key={h.messageId}><button onClick={() => { setSelectedChatId(h.chatId); setQuery(""); }} className="block w-full rounded-xl bg-surface-2 p-3 text-left text-sm hover:bg-surface">
              <span className="flex justify-between gap-2"><span className="truncate font-semibold">{nameOf(h.chatId)}</span><span className="shrink-0 text-xs text-muted">{new Date(h.timestamp).toLocaleDateString()}</span></span>
              <span className="mt-0.5 line-clamp-2 text-muted">{h.fromMe ? "You: " : ""}{h.text}</span>
            </button></li>
          ))}
        </ul>
      ) : (
        <>
          <ul className={`${heightClass} space-y-1 overflow-y-auto`}>
            {shown.length === 0 ? <li className="p-3 text-sm text-muted">{showArchived ? "No archived chats." : "Everything is archived."}</li> : shown.map((c) => <ChatRow key={c.chatId} c={c} connectionId={connectionId} onOpen={() => setSelectedChatId(c.chatId)} onChanged={loadChats} />)}
          </ul>
          {(archivedCount > 0 || showArchived) && <button type="button" onClick={() => setShowArchived((v) => !v)} className="text-xs font-semibold text-muted hover:text-text">{showArchived ? "← Back to chats" : `Archived (${archivedCount})`}</button>}
        </>
      )}
    </div>
  );
}
