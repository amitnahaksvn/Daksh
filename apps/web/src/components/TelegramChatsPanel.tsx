"use client";
import { useCallback, useEffect, useRef, useState } from "react";
import { ChatAvatar } from "@/components/ChatAvatar";
import { TelegramThreadView } from "@/components/TelegramThreadView";
import { EmptyState, inputClass, Loading, tileClass } from "@/components/ui";
import { api, ApiError, type TelegramChat, type TelegramSearchHit } from "@/lib/api";
import { getIntegrationsHub } from "@/lib/signalr";

const isMuted = (c: TelegramChat) => Boolean(c.mutedUntil && new Date(c.mutedUntil).getTime() > Date.now());

function ChatRow({ c, id, active, onOpen, onChanged }: { c: TelegramChat; id: string; active: boolean; onOpen: () => void; onChanged: () => void }) {
  const [menu, setMenu] = useState(false);
  const muted = isMuted(c);
  async function pref(p: { pinned?: boolean; archived?: boolean; muteHours?: number }) { setMenu(false); try { await api.setTelegramChatPrefs(id, c.chatId, p); } finally { onChanged(); } }
  const item = "block w-full rounded-lg px-3 py-1.5 text-left text-xs hover:bg-surface-2";
  return (
    <li className="relative">
      <div className={`flex items-center rounded-xl hover:bg-surface-2 ${active ? "bg-surface-2" : ""}`}>
        <button onClick={onOpen} className="flex min-w-0 flex-1 items-start gap-3 px-3 py-2 text-left">
          <ChatAvatar channel="telegram" connectionId={id} chatId={c.chatId} name={c.name} isGroup={c.isGroup} size={36} />
          <span className="min-w-0 flex-1">
            <span className="flex items-center gap-1.5">
              <span className="truncate text-sm font-semibold">{c.name}</span>
              {c.isGroup && <span className="ai-mono rounded-full bg-surface-2 px-1.5 py-0.5 text-[9px] font-semibold text-muted">GROUP</span>}
              {c.pinned && <span aria-label="Pinned" title="Pinned" className="text-[10px]">📌</span>}
              {muted && <span aria-label="Muted" title="Muted" className="text-[10px]">🔕</span>}
            </span>
            <span className="block truncate text-xs text-muted">{c.lastMessagePreview}</span>
          </span>
          {c.unreadCount > 0 && !active && <span className={`mt-1 flex h-5 min-w-5 items-center justify-center rounded-full px-1 text-[10px] font-bold ${muted ? "bg-surface text-muted" : "bg-pink text-on-tint"}`}>{c.unreadCount}</span>}
        </button>
        <button type="button" onClick={() => setMenu((v) => !v)} aria-label={`Options for ${c.name}`} aria-expanded={menu} className="mr-2 rounded-full px-2 py-1 text-muted hover:bg-surface hover:text-text">⋯</button>
      </div>
      {menu && (<>
        <button type="button" aria-label="Close menu" onClick={() => setMenu(false)} className="fixed inset-0 z-10 cursor-default" />
        <div role="menu" className="absolute right-2 top-full z-20 mt-1 w-44 rounded-xl border border-line bg-surface p-1 shadow-[var(--shadow)]">
          <button role="menuitem" className={item} onClick={() => void pref({ pinned: !c.pinned })}>{c.pinned ? "Unpin" : "Pin to top"}</button>
          <button role="menuitem" className={item} onClick={() => void pref({ archived: !c.archived })}>{c.archived ? "Unarchive" : "Archive"}</button>
          {muted ? <button role="menuitem" className={item} onClick={() => void pref({ muteHours: 0 })}>Unmute</button> : <>
            <button role="menuitem" className={item} onClick={() => void pref({ muteHours: 8 })}>Mute for 8 hours</button>
            <button role="menuitem" className={item} onClick={() => void pref({ muteHours: 24 * 7 })}>Mute for a week</button></>}
        </div>
      </>)}
    </li>
  );
}

/** Chat list + thread + composer for a connected Telegram account; refreshes live over SignalR. */
export function TelegramChatsPanel({ connectionId, heightClass = "h-[calc(100vh-22rem)] min-h-[24rem]", compact = false }: { connectionId: string; heightClass?: string; compact?: boolean }) {
  const id = connectionId;
  const [chats, setChats] = useState<TelegramChat[] | null>(null);
  const [active, setActive] = useState<string | null>(null);
  const [showArchived, setShowArchived] = useState(false);
  const [query, setQuery] = useState("");
  const [hits, setHits] = useState<TelegramSearchHit[] | null>(null);
  const [searchError, setSearchError] = useState<string | null>(null);
  const activeRef = useRef(active);
  useEffect(() => { activeRef.current = active; }, [active]);

  const loadChats = useCallback(() => { api.telegramChats(id).then(setChats).catch(() => setChats((c) => c ?? [])); }, [id]);
  useEffect(loadChats, [loadChats]);
  useEffect(() => {
    const hub = getIntegrationsHub();
    const onMessage = (p: { connectionId: string }) => { if (p.connectionId === id) loadChats(); };
    hub.on("TelegramMessageReceived", onMessage); hub.on("TelegramMessageUpdated", onMessage);
    return () => { hub.off("TelegramMessageReceived", onMessage); hub.off("TelegramMessageUpdated", onMessage); };
  }, [id, loadChats]);

  useEffect(() => {
    const q = query.trim();
    if (q.length < 2) { const t = setTimeout(() => { setHits(null); setSearchError(null); }, 0); return () => clearTimeout(t); }
    const t = setTimeout(() => { api.searchTelegram(id, q).then((h) => { setHits(h); setSearchError(null); }, (e) => setSearchError(e instanceof ApiError ? e.message : "Search failed.")); }, 300);
    return () => clearTimeout(t);
  }, [query, id]);

  const activeChat = chats?.find((c) => c.chatId === active) ?? null;
  const archivedCount = chats?.filter((c) => c.archived).length ?? 0;
  const shown = (chats ?? []).filter((c) => Boolean(c.archived) === showArchived);
  const nameOf = (chatId: string) => chats?.find((c) => c.chatId === chatId)?.name ?? "Chat";

  return (
    <div className={`${tileClass} grid min-h-0 overflow-hidden ${heightClass} ${compact ? "grid-cols-1" : "md:grid-cols-[18rem_1fr]"}`}>
      <aside className={`min-h-0 overflow-y-auto border-line ${compact ? (active ? "hidden" : "") : "border-b md:border-b-0 md:border-r"}`}>
        <div className="p-2"><input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search all messages…" aria-label="Search all messages" className={inputClass} /></div>
        {searchError && <p role="alert" className="px-3 pb-2 text-sm text-danger">{searchError}</p>}
        {hits ? (
          <ul className="p-2" aria-label="Search results">
            {hits.length === 0 ? <li className="p-3 text-sm text-muted">No messages match &ldquo;{query.trim()}&rdquo;.</li> : hits.map((h) => (
              <li key={`${h.chatId}-${h.messageId}`}><button onClick={() => { setActive(h.chatId); setQuery(""); }} className="block w-full rounded-xl px-3 py-2 text-left text-sm hover:bg-surface-2">
                <span className="flex justify-between gap-2"><span className="truncate font-semibold">{nameOf(h.chatId)}</span><span className="shrink-0 text-xs text-muted">{new Date(h.timestamp).toLocaleDateString()}</span></span>
                <span className="line-clamp-2 text-xs text-muted">{h.fromMe ? "You: " : ""}{h.text}</span></button></li>
            ))}
          </ul>
        ) : !chats ? <div className="p-4"><Loading /></div>
          : chats.length === 0 ? <div className="p-4"><EmptyState>No chats yet. Messages you send or receive on Telegram will appear here.</EmptyState></div>
          : <>
            <ul className="p-2">{shown.length === 0 ? <li className="p-3 text-sm text-muted">{showArchived ? "No archived chats." : "Everything is archived."}</li> : shown.map((c) => <ChatRow key={c.chatId} c={c} id={id} active={c.chatId === active} onOpen={() => setActive(c.chatId)} onChanged={loadChats} />)}</ul>
            {(archivedCount > 0 || showArchived) && <button type="button" onClick={() => setShowArchived((v) => !v)} className="mx-3 mb-3 text-xs font-semibold text-muted hover:text-text">{showArchived ? "← Back to chats" : `Archived (${archivedCount})`}</button>}
          </>}
      </aside>

      <section className={`flex min-h-0 flex-col ${compact && !active ? "hidden" : ""}`}>
        {!activeChat ? <div className="m-auto p-6 text-sm text-muted">Pick a chat to read and reply.</div> : (
          <>
            {compact && <button type="button" onClick={() => setActive(null)} aria-label="Back to chats" className="self-start px-4 py-2 text-sm text-muted hover:text-text">← Chats</button>}
            <TelegramThreadView key={activeChat.chatId} connectionId={id} chat={activeChat} onSent={loadChats} bodyHeightClass="" />
          </>
        )}
      </section>
    </div>
  );
}

/** One Telegram conversation, used by the Chats side panel. */
export function TelegramThread({ connectionId, chat, onBack, heightClass, embedded = false }: { connectionId: string; chat: TelegramChat; onBack: () => void; heightClass: string; embedded?: boolean }) {
  return (
    <div className={`flex min-h-0 flex-col ${heightClass}`}>
      {!embedded && <header className="flex items-center gap-2 border-b border-line px-3 py-2 text-sm font-semibold"><button type="button" onClick={onBack} aria-label="Back to chats" className="rounded-full px-2 py-0.5 text-muted hover:bg-surface-2 hover:text-text">←</button><span className="truncate">{chat.name}</span></header>}
      <TelegramThreadView connectionId={connectionId} chat={chat} embedded bodyHeightClass="" />
    </div>
  );
}
