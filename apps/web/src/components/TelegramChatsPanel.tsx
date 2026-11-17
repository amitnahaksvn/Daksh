"use client";
import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { api, ApiError, type TelegramChat, type TelegramMessage } from "@/lib/api";
import { getIntegrationsHub } from "@/lib/signalr";
import { buttonClass, EmptyState, inputClass, Loading, tileClass } from "@/components/ui";

/** Chat list + thread + composer for a connected Telegram account; refreshes live over SignalR. */
export function TelegramChatsPanel({ connectionId, heightClass = "h-[calc(100vh-22rem)] min-h-[24rem]", compact = false }: { connectionId: string; heightClass?: string; compact?: boolean }) {
  const id = connectionId;
  const [chats, setChats] = useState<TelegramChat[] | null>(null);
  const [active, setActive] = useState<string | null>(null);
  const [messages, setMessages] = useState<TelegramMessage[]>([]);
  const [draft, setDraft] = useState("");
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const bottomRef = useRef<HTMLDivElement>(null);
  const activeRef = useRef(active);
  useEffect(() => { activeRef.current = active; }, [active]);

  const loadChats = useCallback(() => {
    api.telegramChats(id).then(setChats).catch(() => setChats((c) => c ?? []));
  }, [id]);
  const loadThread = useCallback((chatId: string) => {
    api.telegramMessages(id, chatId).then(setMessages).catch(() => undefined);
  }, [id]);

  useEffect(loadChats, [loadChats]);
  useEffect(() => { if (active) loadThread(active); }, [active, loadThread]);
  useEffect(() => bottomRef.current?.scrollIntoView({ block: "end" }), [messages]);

  useEffect(() => {
    const hub = getIntegrationsHub();
    const onMessage = (p: { connectionId: string; chatId?: string }) => {
      if (p.connectionId !== id) return;
      loadChats();
      if (p.chatId && p.chatId === activeRef.current) loadThread(p.chatId);
    };
    hub.on("TelegramMessageReceived", onMessage);
    return () => hub.off("TelegramMessageReceived", onMessage);
  }, [id, loadChats, loadThread]);

  async function send(e: FormEvent) {
    e.preventDefault();
    if (!active || !draft.trim()) return;
    setSending(true);
    setError(null);
    try {
      const sent = await api.sendTelegramMessage(id, active, draft.trim());
      setMessages((m) => (m.some((x) => x.messageId === sent.messageId) ? m : [...m, sent]));
      setDraft("");
      loadChats();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not send the message.");
    } finally {
      setSending(false);
    }
  }

  const activeChat = chats?.find((c) => c.chatId === active);

  return (
    <div className={`${compact ? "" : tileClass} grid overflow-hidden ${compact ? "grid-cols-1" : "md:grid-cols-[18rem_1fr]"} ${heightClass}`}>
      <aside className={`min-h-0 overflow-y-auto ${compact ? (active ? "hidden" : "") : "border-b border-line md:border-b-0 md:border-r"}`}>
        {!chats ? <Loading /> : chats.length === 0 ? (
          <div className="p-4"><EmptyState>No chats yet. Messages you send or receive on Telegram will appear here.</EmptyState></div>
        ) : (
          <ul className="p-2">
            {chats.map((c) => (
              <li key={c.chatId}>
                <button
                  onClick={() => setActive(c.chatId)}
                  className={`flex w-full items-start gap-3 rounded-xl px-3 py-2 text-left hover:bg-surface-2 ${c.chatId === active ? "bg-surface-2" : ""}`}
                >
                  <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-lime text-sm font-bold text-on-tint">{c.name.slice(0, 1).toUpperCase()}</span>
                  <span className="min-w-0 flex-1">
                    <span className="flex items-center gap-1.5">
                      <span className="truncate text-sm font-semibold">{c.name}</span>
                      {c.isGroup && <span className="ai-mono rounded-full bg-surface-2 px-1.5 py-0.5 text-[9px] font-semibold text-muted">GROUP</span>}
                    </span>
                    <span className="block truncate text-xs text-muted">{c.lastMessagePreview}</span>
                  </span>
                  {c.unreadCount > 0 && c.chatId !== active && (
                    <span className="mt-1 flex h-5 min-w-5 items-center justify-center rounded-full bg-pink px-1 text-[10px] font-bold text-on-tint">{c.unreadCount}</span>
                  )}
                </button>
              </li>
            ))}
          </ul>
        )}
      </aside>

      <section className={`flex min-h-0 flex-col ${compact && !active ? "hidden" : ""}`}>
        {!active ? (
          <div className="m-auto p-6 text-sm text-muted">Pick a chat to read and reply.</div>
        ) : (
          <>
            <header className="flex items-center gap-2 border-b border-line px-4 py-3 text-sm font-semibold">
              {compact && <button type="button" onClick={() => setActive(null)} aria-label="Back to chats" className="-ml-2 rounded-full px-2 py-0.5 text-muted hover:bg-surface-2 hover:text-text">←</button>}
              <span className="truncate">{activeChat?.name ?? active}</span>
            </header>
            <div className="flex min-h-0 flex-1 flex-col gap-2 overflow-y-auto p-4">
              {messages.map((m) => (
                <div key={m.messageId} className={`max-w-[80%] rounded-2xl px-3.5 py-2 text-sm ${m.fromMe ? "self-end bg-accent text-accent-contrast" : "self-start bg-surface-2"}`}>
                  {!m.fromMe && m.senderName && activeChat?.isGroup && <p className="text-[11px] font-semibold text-muted">{m.senderName}</p>}
                  <p className="whitespace-pre-wrap break-words">{m.text}</p>
                  <p className="mt-0.5 text-[10px] opacity-70">
                    {m.isAiGenerated ? "🤖 Daksha · " : ""}{new Date(m.timestamp).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}
                  </p>
                </div>
              ))}
              <div ref={bottomRef} />
            </div>
            <form onSubmit={send} className="flex gap-2 border-t border-line p-3">
              <input value={draft} onChange={(e) => setDraft(e.target.value)} placeholder="Write a message…" className={inputClass} />
              <button type="submit" disabled={sending || !draft.trim()} className={`${buttonClass} shrink-0 px-5 py-2 text-sm disabled:opacity-50`}>Send</button>
            </form>
            {error && <p role="alert" className="px-4 pb-3 text-sm text-danger">{error}</p>}
          </>
        )}
      </section>
    </div>
  );
}

/** One Telegram conversation: messages, live refresh and a composer. Used by the Chats side panel. */
export function TelegramThread({ connectionId, chat, onBack, heightClass, embedded = false }: { connectionId: string; chat: TelegramChat; onBack: () => void; heightClass: string; embedded?: boolean }) {
  const [messages, setMessages] = useState<TelegramMessage[]>([]);
  const [draft, setDraft] = useState("");
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const bottomRef = useRef<HTMLDivElement>(null);

  const load = useCallback(() => {
    api.telegramMessages(connectionId, chat.chatId).then(setMessages).catch(() => undefined);
  }, [connectionId, chat.chatId]);

  useEffect(load, [load]);
  useEffect(() => bottomRef.current?.scrollIntoView({ block: "end" }), [messages]);
  useEffect(() => {
    const hub = getIntegrationsHub();
    const onMessage = (p: { connectionId: string; chatId?: string }) => {
      if (p.connectionId === connectionId && p.chatId === chat.chatId) load();
    };
    hub.on("TelegramMessageReceived", onMessage);
    return () => hub.off("TelegramMessageReceived", onMessage);
  }, [connectionId, chat.chatId, load]);

  async function send(e: FormEvent) {
    e.preventDefault();
    if (!draft.trim()) return;
    setSending(true);
    setError(null);
    try {
      const sent = await api.sendTelegramMessage(connectionId, chat.chatId, draft.trim());
      setMessages((m) => (m.some((x) => x.messageId === sent.messageId) ? m : [...m, sent]));
      setDraft("");
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not send the message.");
    } finally {
      setSending(false);
    }
  }

  return (
    <div className={`flex flex-col overflow-hidden ${embedded ? "" : "rounded-[1.25rem] border border-line"} ${heightClass}`}>
      {!embedded && (
        <header className="flex items-center gap-2 border-b border-line px-3 py-2.5 text-sm font-semibold">
          <button type="button" onClick={onBack} aria-label="Back to chats" className="rounded-full px-2 py-0.5 text-muted hover:bg-surface-2 hover:text-text">←</button>
          <span className="truncate">{chat.name}</span>
          {chat.isGroup && <span className="ai-mono rounded-full bg-surface-2 px-1.5 py-0.5 text-[9px] font-semibold text-muted">GROUP</span>}
        </header>
      )}
      <div className="flex min-h-0 flex-1 flex-col gap-2 overflow-y-auto p-3">
        {messages.map((m) => (
          <div key={m.messageId} className={`max-w-[85%] rounded-2xl px-3.5 py-2 text-sm ${m.fromMe ? "self-end bg-accent text-accent-contrast" : "self-start bg-surface-2"}`}>
            {!m.fromMe && m.senderName && chat.isGroup && <p className="text-[11px] font-semibold text-muted">{m.senderName}</p>}
            <p className="whitespace-pre-wrap break-words">{m.text}</p>
            <p className="mt-0.5 text-[10px] opacity-70">{m.isAiGenerated ? "🤖 Daksha · " : ""}{new Date(m.timestamp).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</p>
          </div>
        ))}
        <div ref={bottomRef} />
      </div>
      <form onSubmit={send} className="flex gap-2 border-t border-line p-3">
        <input value={draft} onChange={(e) => setDraft(e.target.value)} placeholder="Reply…" className={inputClass} />
        <button type="submit" disabled={sending || !draft.trim()} className={`${buttonClass} px-4 disabled:opacity-50`}>Send</button>
      </form>
      {error && <p role="alert" className="px-3 pb-3 text-sm text-danger">{error}</p>}
    </div>
  );
}
