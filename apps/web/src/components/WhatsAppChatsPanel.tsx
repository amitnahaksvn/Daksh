"use client";
import { useCallback, useEffect, useRef, useState } from "react";
import { buttonClass, inputClass, secondaryButtonClass } from "@/components/ui";
import { api, ApiError, type WhatsAppChat, type WhatsAppMessage, type WhatsAppReaction } from "@/lib/api";
import { getIntegrationsHub } from "@/lib/signalr";

/** The contact's name when known; otherwise a readable fallback instead of a raw WhatsApp id — a real phone
 *  number for a normal chat, and a plain label for a privacy-masked `@lid` id (which isn't a phone number). */
export function chatDisplayName(chat: WhatsAppChat): string {
  if (chat.chatName) return chat.chatName;
  if (chat.isGroup) return "WhatsApp group";
  if (chat.chatId === "status@broadcast") return "Status updates";
  if (chat.chatId.endsWith("@s.whatsapp.net")) return `+${chat.chatId.split("@")[0]}`;
  return "Unknown contact";
}

export function GroupBadge() {
  return <span className="ai-mono inline-flex items-center rounded-full bg-surface-2 px-2 py-0.5 text-[10px] font-semibold text-muted">GROUP</span>;
}

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

const REACTION_EMOJIS = ["👍", "❤️", "😂", "😮", "😢", "🙏"];

function groupReactions(reactions: WhatsAppReaction[]): [string, number][] {
  const counts = new Map<string, number>();
  for (const r of reactions) counts.set(r.emoji, (counts.get(r.emoji) ?? 0) + 1);
  return [...counts.entries()];
}

/** Photos need the bearer token, so they're fetched as a blob rather than used as a plain <img src>. */
function MessageImage({ connectionId, messageId, onLoaded }: { connectionId: string; messageId: string; onLoaded: () => void }) {
  const [src, setSrc] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    let url: string | null = null;
    let cancelled = false;
    api
      .whatsAppMedia(connectionId, messageId)
      .then((blob) => {
        if (cancelled) return;
        url = URL.createObjectURL(blob);
        setSrc(url);
      })
      .catch(() => !cancelled && setFailed(true));
    return () => {
      cancelled = true;
      if (url) URL.revokeObjectURL(url);
    };
  }, [connectionId, messageId]);
  if (failed) return null;
  if (!src) return <div className="mb-1 h-40 w-56 animate-pulse rounded-lg bg-black/10" />;
  // eslint-disable-next-line @next/next/no-img-element
  return <a href={src} target="_blank" rel="noreferrer"><img src={src} onLoad={onLoaded} alt="WhatsApp photo" className="mb-1 max-h-72 max-w-full rounded-lg object-contain" /></a>;
}

function MessageBubble({
  m,
  chat,
  connectionId,
  onImageLoaded,
  onReply,
  onReact,
}: {
  connectionId: string;
  onImageLoaded: () => void;
  m: WhatsAppMessage;
  chat: WhatsAppChat;
  onReply: () => void;
  onReact: (emoji: string) => void;
}) {
  const [pickerOpen, setPickerOpen] = useState(false);
  return (
    <div className={`relative max-w-[85%] rounded-xl p-2.5 text-sm ${m.fromMe ? "ml-auto bg-accent text-accent-contrast" : "bg-surface-2"}`}>
      {!m.fromMe && chat.isGroup && <p className="text-xs font-semibold opacity-70">{m.chatName ?? "Unknown"}</p>}
      {m.quotedText && (
        <div className={`mb-1 truncate rounded-lg border-l-2 px-2 py-1 text-xs opacity-80 ${m.fromMe ? "border-accent-contrast/50 bg-black/10" : "border-line bg-surface"}`}>
          {m.quotedText}
        </div>
      )}
      {m.hasMedia && <MessageImage connectionId={connectionId} messageId={m.messageId} onLoaded={onImageLoaded} />}
      {!(m.hasMedia && (m.text === "📷 Photo" || m.text === "Sticker")) && <p>{m.text}</p>}
      {m.reactions.length > 0 && (
        <div className="mt-1 flex flex-wrap gap-1">
          {groupReactions(m.reactions).map(([emoji, count]) => (
            <span key={emoji} className="rounded-full bg-black/10 px-1.5 py-0.5 text-xs">
              {emoji}
              {count > 1 ? ` ${count}` : ""}
            </span>
          ))}
        </div>
      )}
      <div className="mt-0.5 flex items-center justify-end gap-2">
        <button type="button" onClick={() => setPickerOpen((v) => !v)} aria-label="React" className="text-xs opacity-60 hover:opacity-100">
          🙂+
        </button>
        <button type="button" onClick={onReply} aria-label="Reply" className="text-xs opacity-60 hover:opacity-100">
          ↩
        </button>
        <span className="text-[10px] opacity-70">{new Date(m.timestamp).toLocaleString()}</span>
      </div>
      {m.fromMe && m.isAiGenerated && <p className="mt-0.5 text-right text-[9px] italic opacity-60">🤖 Reply by AI Assistant</p>}
      {pickerOpen && (
        <div className="absolute -top-10 right-0 z-10 flex gap-1 rounded-full border border-line bg-surface p-1.5 text-text shadow-[var(--shadow)]">
          {REACTION_EMOJIS.map((emoji) => (
            <button
              key={emoji}
              type="button"
              onClick={() => {
                onReact(emoji);
                setPickerOpen(false);
              }}
              className="text-base transition-transform hover:scale-125"
            >
              {emoji}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

function ChatThread({
  connectionId,
  chat,
  onClose,
  onSent,
  bodyHeightClass = "max-h-80",
}: {
  connectionId: string;
  chat: WhatsAppChat;
  onClose: () => void;
  onSent: () => void;
  bodyHeightClass?: string;
}) {
  const [messages, setMessages] = useState<WhatsAppMessage[] | null>(null);
  const [text, setText] = useState("");
  const [replyingTo, setReplyingTo] = useState<WhatsAppMessage | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const lastSeenId = useRef<string | null>(null);

  // Always land on the newest message when a chat opens, and follow along as new ones arrive.
  useEffect(() => {
    const last = messages?.[messages.length - 1]?.messageId ?? null;
    if (last === lastSeenId.current) return;
    lastSeenId.current = last;
    const el = scrollRef.current;
    if (el) requestAnimationFrame(() => el.scrollTo({ top: el.scrollHeight }));
  }, [messages]);

  // A photo finishing loading grows the thread; if the reader was at the bottom, stay there.
  const keepAtBottom = useCallback(() => {
    const el = scrollRef.current;
    if (el && el.scrollHeight - el.scrollTop - el.clientHeight < 450) el.scrollTo({ top: el.scrollHeight });
  }, []);

  const load = useCallback(async () => {
    try {
      setMessages(await api.whatsAppChatMessages(connectionId, chat.chatId));
    } catch {
      /* best-effort */
    }
  }, [connectionId, chat.chatId]);

  useEffect(() => {
    const timer = setTimeout(() => void load(), 0);
    return () => clearTimeout(timer);
  }, [load]);

  useEffect(() => {
    const hub = getIntegrationsHub();
    const onMessage = (payload: { connectionId: string; chatId?: string }) => {
      if (payload.connectionId === connectionId && payload.chatId === chat.chatId) void load();
    };
    // Covers an incoming message, anything sent from here (manual reply, auto-reply, another open tab),
    // and a reaction arriving on an existing message.
    hub.on("WhatsAppMessageReceived", onMessage);
    hub.on("WhatsAppMessageSent", onMessage);
    hub.on("WhatsAppMessageUpdated", onMessage);
    return () => {
      hub.off("WhatsAppMessageReceived", onMessage);
      hub.off("WhatsAppMessageSent", onMessage);
      hub.off("WhatsAppMessageUpdated", onMessage);
    };
  }, [connectionId, chat.chatId, load]);

  // SignalR should deliver this instantly, but a dropped/stuck connection has repeatedly left this thread
  // stale until a manual refresh — same situation the QR/connecting flow already handles by polling
  // alongside the push. Poll here too so an open thread never silently goes stale.
  useEffect(() => {
    const interval = setInterval(() => void load(), 4000);
    return () => clearInterval(interval);
  }, [load]);

  async function send() {
    if (!text.trim()) return;
    setError(null);
    setBusy(true);
    try {
      await api.sendWhatsAppChatMessage(connectionId, chat.chatId, text, replyingTo?.messageId);
      setText("");
      setReplyingTo(null);
      await load();
      onSent();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not send the message.");
    } finally {
      setBusy(false);
    }
  }

  async function react(messageId: string, emoji: string) {
    try {
      await api.reactToWhatsAppMessage(connectionId, messageId, emoji);
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not react to the message.");
    }
  }

  return (
    <div className="rounded-[1.25rem] border border-line">
      <div className="flex items-center justify-between gap-2 border-b border-line p-3">
        <div className="flex items-center gap-2">
          <span className="font-semibold">{chatDisplayName(chat)}</span>
          {chat.isGroup && <GroupBadge />}
        </div>
        <button onClick={onClose} className="text-sm text-muted hover:text-text">Close</button>
      </div>
      <div ref={scrollRef} className={`${bodyHeightClass} space-y-2 overflow-y-auto p-3`}>
        {!messages ? (
          <p className="text-sm text-muted">Loading…</p>
        ) : messages.length === 0 ? (
          <p className="text-sm text-muted">No messages yet.</p>
        ) : (
          messages.map((m) => (
            <MessageBubble key={m.messageId} m={m} chat={chat} connectionId={connectionId} onImageLoaded={keepAtBottom} onReply={() => setReplyingTo(m)} onReact={(emoji) => void react(m.messageId, emoji)} />
          ))
        )}
      </div>
      {replyingTo && (
        <div className="flex items-center justify-between gap-2 border-t border-line bg-surface-2 px-3 py-2 text-xs">
          <span className="truncate">
            Replying to: <span className="opacity-70">{replyingTo.text}</span>
          </span>
          <button onClick={() => setReplyingTo(null)} aria-label="Cancel reply" className="shrink-0 text-muted hover:text-text">
            ✕
          </button>
        </div>
      )}
      <div className="flex gap-2 border-t border-line p-3">
        <input
          value={text}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => { if (e.key === "Enter") void send(); }}
          placeholder="Reply…"
          className={inputClass}
        />
        <button onClick={send} disabled={busy || !text.trim()} className={`${buttonClass} px-4`}>Send</button>
      </div>
      {error && <p role="alert" className="px-3 pb-3 text-sm text-danger">{error}</p>}
    </div>
  );
}

/** The chat list + thread view for one WhatsApp connection — the "see every message and reply right here"
 *  surface, shared between the Manage panel and the bigger Dashboard card so they stay identical. */
export function WhatsAppChatsPanel({ connectionId, heightClass = "h-[calc(100vh-22rem)] min-h-[24rem]" }: { connectionId: string; heightClass?: string }) {
  const [chats, setChats] = useState<WhatsAppChat[] | null>(null);
  const [selectedChatId, setSelectedChatId] = useState<string | null>(null);

  const loadChats = useCallback(async () => {
    try {
      setChats(await api.whatsAppChats(connectionId));
    } catch {
      /* best-effort; the panel still works without the chat list */
    }
  }, [connectionId]);

  useEffect(() => {
    const timer = setTimeout(() => void loadChats(), 0);
    return () => clearTimeout(timer);
  }, [loadChats]);

  // Push-plus-poll fallback, same reasoning as ChatThread: SignalR should make this instant, but a dead
  // connection has repeatedly left the chat list stale until a manual refresh.
  useEffect(() => {
    const interval = setInterval(() => void loadChats(), 5000);
    return () => clearInterval(interval);
  }, [loadChats]);

  useEffect(() => {
    const hub = getIntegrationsHub();
    const onMessage = (payload: { connectionId: string }) => {
      if (payload.connectionId === connectionId) void loadChats();
    };
    hub.on("WhatsAppMessageReceived", onMessage);
    hub.on("WhatsAppMessageSent", onMessage);
    if (hub.state === "Disconnected") hub.start().catch(() => void 0);
    return () => {
      hub.off("WhatsAppMessageReceived", onMessage);
      hub.off("WhatsAppMessageSent", onMessage);
    };
  }, [connectionId, loadChats]);

  const selectedChat = chats?.find((c) => c.chatId === selectedChatId) ?? null;

  if (selectedChat) {
    return <ChatThread connectionId={connectionId} chat={selectedChat} onClose={() => setSelectedChatId(null)} onSent={loadChats} bodyHeightClass={heightClass} />;
  }

  if (!chats) return <p className="text-sm text-muted">Loading…</p>;
  if (chats.length === 0) return <p className="text-sm text-muted">No chats yet. They&rsquo;ll show up here as messages arrive.</p>;

  return (
    <ul className={`${heightClass} space-y-1 overflow-y-auto`}>
      {chats.map((c) => (
        <li key={c.chatId}>
          <button
            onClick={() => setSelectedChatId(c.chatId)}
            className="flex w-full items-center gap-3 rounded-xl bg-surface-2 p-3 text-left text-sm hover:bg-surface"
          >
            <div className="min-w-0 flex-1">
              <div className="flex items-center gap-2">
                <span className="truncate font-semibold">{chatDisplayName(c)}</span>
                {c.isGroup && <GroupBadge />}
              </div>
              <p className="mt-0.5 truncate text-muted">{c.lastMessagePreview}</p>
            </div>
            <div className="flex shrink-0 flex-col items-end gap-1">
              <span className="text-xs text-muted">{new Date(c.lastMessageAt).toLocaleString()}</span>
              {c.unreadCount > 0 && (
                <span className="flex h-5 min-w-[1.25rem] items-center justify-center rounded-full bg-pink px-1.5 text-[10px] font-bold text-on-tint">
                  {c.unreadCount > 9 ? "9+" : c.unreadCount}
                </span>
              )}
            </div>
          </button>
        </li>
      ))}
    </ul>
  );
}
