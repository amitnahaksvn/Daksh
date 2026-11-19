"use client";
import { useCallback, useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { ChatAvatar } from "@/components/ChatAvatar";
import { Linkified } from "@/components/Linkified";
import { AssistBar, DayDivider, EmojiButton, exportChat, sameDay } from "@/components/ChatExtras";
import { buttonClass, inputClass } from "@/components/ui";
import { api, ApiError, type WhatsAppChat, type WhatsAppGroupInfo, type WhatsAppMessage, type WhatsAppPresence, type WhatsAppReaction } from "@/lib/api";
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

const REACTION_EMOJIS = ["👍", "❤️", "😂", "😮", "😢", "🙏"];
const EDIT_WINDOW_MS = 15 * 60 * 1000;
const DELETE_FOR_EVERYONE_MS = 60 * 3600 * 1000; // WhatsApp allows about two and a half days

function groupReactions(reactions: WhatsAppReaction[]): [string, number][] {
  const counts = new Map<string, number>();
  for (const r of reactions) counts.set(r.emoji, (counts.get(r.emoji) ?? 0) + 1);
  return [...counts.entries()];
}

const fileSize = (bytes: number) => (bytes > 1048576 ? `${(bytes / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`);

/** The ticks on a message you sent: clock (sending), one grey (sent), two grey (delivered), two blue (read). */
export function MessageTicks({ status }: { status: number | null }) {
  if (status === null || status === undefined) return null;
  const label = status >= 4 ? "Read" : status === 3 ? "Delivered" : status === 2 ? "Sent" : status === 1 ? "Sending" : "Failed";
  const colour = status >= 4 ? "var(--wa-tick, #38bdf8)" : "currentColor";
  return (
    <span role="img" aria-label={label} title={label} className={status >= 4 ? "" : "opacity-70"} style={{ color: colour }}>
      <svg viewBox="0 0 20 12" width="16" height="10" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round">
        {status === 1 ? <><circle cx="10" cy="6" r="4.2" /><path d="M10 3.8V6l1.5 1" /></>
          : status === 0 ? <path d="M6 2l8 8M14 2l-8 8" />
          : status === 2 ? <path d="M5 6.5l3 3 7-8" />
          : <><path d="M2 6.5l3 3 7-8" /><path d="M9 8.8l1 1 7-8" /></>}
      </svg>
    </span>
  );
}

/** Photos, video, voice notes and files need the bearer token, so they are fetched as a blob rather than used as a plain src. */
function MessageMedia({ connectionId, m, onLoaded }: { connectionId: string; m: WhatsAppMessage; onLoaded: () => void }) {
  const [src, setSrc] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    let url: string | null = null;
    let cancelled = false;
    api.whatsAppMedia(connectionId, m.messageId)
      .then((blob) => { if (cancelled) return; url = URL.createObjectURL(blob); setSrc(url); })
      .catch(() => !cancelled && setFailed(true));
    return () => { cancelled = true; if (url) URL.revokeObjectURL(url); };
  }, [connectionId, m.messageId]);
  if (failed) return null;
  const kind = m.mediaKind ?? "image";
  if (!src) return <div className={`mb-1 animate-pulse rounded-lg bg-black/10 ${kind === "audio" ? "h-10 w-56" : kind === "document" ? "h-12 w-56" : "h-40 w-56"}`} />;
  if (kind === "video") return <video src={src} controls preload="metadata" onLoadedMetadata={onLoaded} className="mb-1 max-h-72 max-w-full rounded-lg" />;
  if (kind === "audio") return <audio src={src} controls preload="metadata" onLoadedMetadata={onLoaded} className="mb-1 w-60 max-w-full" aria-label={m.voiceNote ? "Voice message" : "Audio"} />;
  if (kind === "document") {
    return (
      <a href={src} download={m.fileName ?? "file"} className="mb-1 flex items-center gap-2 rounded-lg bg-black/10 px-3 py-2 text-xs font-semibold hover:bg-black/15">
        <span aria-hidden>📄</span><span className="min-w-0 truncate">{m.fileName ?? "Document"}</span><span className="shrink-0 opacity-70">Download</span>
      </a>
    );
  }
  // eslint-disable-next-line @next/next/no-img-element
  return <a href={src} target="_blank" rel="noreferrer"><img src={src} onLoad={onLoaded} alt={kind === "sticker" ? "Sticker" : "WhatsApp photo"} className={`mb-1 rounded-lg object-contain ${kind === "sticker" ? "max-h-32" : "max-h-72 max-w-full"}`} /></a>;
}

const LABELS = new Set(["📷 Photo", "🎥 Video", "🎤 Voice message", "📄 Document", "Sticker"]);

function MessageBubble({ m, chat, connectionId, onMediaLoaded, onReply, onReact, onForward, onEdit, onDelete, onStar, onTranslate, translation }: {
  m: WhatsAppMessage; chat: WhatsAppChat; connectionId: string; onMediaLoaded: () => void;
  onReply: () => void; onReact: (emoji: string) => void; onForward: () => void; onEdit: () => void; onDelete: (forEveryone: boolean) => void; onStar: () => void;
  onTranslate: () => void; translation?: string;
}) {
  const [menu, setMenu] = useState<"closed" | "actions" | "react" | "delete">("closed");
  // How old the message is matters only while the menu is open, so the clock is read when it opens (not on every render).
  const [openedAt, setOpenedAt] = useState(0);
  const age = openedAt - new Date(m.timestamp).getTime();
  const canEdit = m.fromMe && !m.deleted && !m.mediaKind && age < EDIT_WINDOW_MS;
  const canDeleteForAll = m.fromMe && !m.deleted && age < DELETE_FOR_EVERYONE_MS;
  const close = () => setMenu("closed");
  const item = "block w-full rounded-lg px-3 py-1.5 text-left text-xs hover:bg-surface-2";

  if (m.deleted) {
    return (
      <div className={`max-w-[85%] rounded-lg px-3 py-1.5 text-[13px] italic text-[var(--wa-meta)] shadow-sm ${m.fromMe ? "ml-auto rounded-tr-none bg-[var(--wa-out)]" : "rounded-tl-none bg-[var(--wa-in)]"}`}>
        🚫 {m.fromMe ? "You deleted this message" : "This message was deleted"}
      </div>
    );
  }
  const showText = !(m.hasMedia && LABELS.has(m.text));
  return (
    <div className={`group relative max-w-[85%] rounded-lg px-2.5 py-1.5 text-[14px] leading-snug text-[var(--wa-text)] shadow-sm ${m.fromMe ? "ml-auto rounded-tr-none bg-[var(--wa-out)]" : "rounded-tl-none bg-[var(--wa-in)]"}`}>
      {!m.fromMe && chat.isGroup && <p className="text-xs font-medium text-[var(--wa-accent)]">{m.chatName ?? "Unknown"}</p>}
      {m.quotedText && (
        <div className={`mb-1 truncate rounded-md border-l-4 border-[var(--wa-accent)] px-2 py-1 text-xs text-[var(--wa-meta)] ${m.fromMe ? "bg-[var(--wa-quote-out)]" : "bg-[var(--wa-quote-in)]"}`}>{m.quotedText}</div>
      )}
      {m.hasMedia && m.mediaKind && <MessageMedia connectionId={connectionId} m={m} onLoaded={onMediaLoaded} />}
      {!m.hasMedia && m.mediaKind === "video" && <p className="opacity-80">🎥 Video</p>}
      {showText && <p className="whitespace-pre-wrap break-words"><Linkified text={m.text} /></p>}
      {translation && <p className="mt-1 whitespace-pre-wrap break-words rounded-lg bg-black/10 px-2 py-1 text-xs"><span aria-hidden>🌐 </span>{translation}</p>}
      {m.reactions.length > 0 && (
        <div className="mt-1 flex flex-wrap gap-1">
          {groupReactions(m.reactions).map(([emoji, count]) => <span key={emoji} className="rounded-full bg-black/10 px-1.5 py-0.5 text-xs">{emoji}{count > 1 ? ` ${count}` : ""}</span>)}
        </div>
      )}
      <div className="-mb-0.5 mt-0.5 flex items-center justify-end gap-1.5 text-[var(--wa-meta)]">
        {m.starred && <span aria-label="Starred" title="Starred" className="text-xs">★</span>}
        {m.edited && <span className="text-[10px] italic opacity-70">edited</span>}
        <span className="text-[10px] opacity-70">{new Date(m.timestamp).toLocaleString([], { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" })}</span>
        {m.fromMe && <MessageTicks status={m.status} />}
        <button type="button" onClick={() => { setOpenedAt(Date.now()); setMenu(menu === "closed" ? "actions" : "closed"); }} aria-label="Message options" aria-expanded={menu !== "closed"} className="rounded px-1 text-xs opacity-0 transition-opacity focus:opacity-100 group-hover:opacity-80">⋯</button>
      </div>
      {m.fromMe && m.isAiGenerated && <p className="mt-0.5 text-right text-[9px] italic opacity-60">🤖 Reply by AI Assistant</p>}

      {menu !== "closed" && (
        <>
          <button type="button" aria-label="Close menu" onClick={close} className="fixed inset-0 z-10 cursor-default" />
          <div role="menu" className={`absolute z-20 w-44 rounded-xl border border-line bg-surface p-1 text-text shadow-[var(--shadow)] ${m.fromMe ? "right-0" : "left-0"} top-full mt-1`}>
            {menu === "actions" && (
              <>
                <button role="menuitem" className={item} onClick={() => setMenu("react")}>React</button>
                <button role="menuitem" className={item} onClick={() => { onReply(); close(); }}>Reply</button>
                <button role="menuitem" className={item} onClick={() => { void navigator.clipboard?.writeText(m.text); close(); }}>Copy</button>
                <button role="menuitem" className={item} onClick={() => { onForward(); close(); }}>Forward</button>
                {m.text && !m.mediaKind && <button role="menuitem" className={item} onClick={() => { onTranslate(); close(); }}>Translate</button>}
                <button role="menuitem" className={item} onClick={() => { onStar(); close(); }}>{m.starred ? "Unstar" : "Star"}</button>
                {canEdit && <button role="menuitem" className={item} onClick={() => { onEdit(); close(); }}>Edit</button>}
                <button role="menuitem" className={`${item} text-danger`} onClick={() => setMenu("delete")}>Delete…</button>
              </>
            )}
            {menu === "react" && (
              <div className="flex flex-wrap gap-1 p-1">{REACTION_EMOJIS.map((emoji) => <button key={emoji} type="button" onClick={() => { onReact(emoji); close(); }} className="text-lg transition-transform hover:scale-125">{emoji}</button>)}</div>
            )}
            {menu === "delete" && (
              <>
                <p className="px-3 py-1 text-[11px] text-muted">Delete this message?</p>
                {canDeleteForAll && <button role="menuitem" className={`${item} text-danger`} onClick={() => { onDelete(true); close(); }}>Delete for everyone</button>}
                <button role="menuitem" className={`${item} text-danger`} onClick={() => { onDelete(false); close(); }}>Delete for me</button>
                <button role="menuitem" className={item} onClick={close}>Cancel</button>
                <p className="px-3 pb-1 pt-0.5 text-[10px] text-muted">&ldquo;For me&rdquo; removes it from Daksha only.</p>
              </>
            )}
          </div>
        </>
      )}
    </div>
  );
}

function ForwardDialog({ connectionId, onPick, onClose }: { connectionId: string; onPick: (chat: WhatsAppChat) => void; onClose: () => void }) {
  const [chats, setChats] = useState<WhatsAppChat[] | null>(null);
  const [q, setQ] = useState("");
  useEffect(() => { void api.whatsAppChats(connectionId).then(setChats, () => setChats([])); }, [connectionId]);
  const shown = (chats ?? []).filter((c) => chatDisplayName(c).toLowerCase().includes(q.trim().toLowerCase()));
  return createPortal(
    <div role="dialog" aria-modal="true" aria-label="Forward to" className="fixed inset-0 z-[1000] flex items-end justify-center bg-black/50 sm:items-center sm:p-6" onClick={onClose}>
      <div className="max-h-[80vh] w-full max-w-md overflow-y-auto rounded-t-3xl bg-canvas p-4 text-text shadow-2xl sm:rounded-3xl" onClick={(e) => e.stopPropagation()}>
        <div className="mb-3 flex items-center justify-between"><h2 className="text-lg font-bold">Forward to</h2><button type="button" onClick={onClose} className="text-sm text-muted">Cancel</button></div>
        <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search chats…" className={`${inputClass} mb-2`} autoFocus />
        {!chats ? <p className="text-sm text-muted">Loading…</p> : shown.length === 0 ? <p className="text-sm text-muted">No chats found.</p> : (
          <ul>{shown.map((c) => (
            <li key={c.chatId}><button type="button" onClick={() => onPick(c)} className="flex w-full items-center gap-3 rounded-xl px-2 py-2 text-left hover:bg-surface-2">
              <ChatAvatar connectionId={connectionId} chatId={c.chatId} name={chatDisplayName(c)} isGroup={c.isGroup} size={36} /><span className="min-w-0 flex-1 truncate text-sm font-semibold">{chatDisplayName(c)}</span>
            </button></li>
          ))}</ul>
        )}
      </div>
    </div>, document.body);
}

function GroupInfoPanel({ connectionId, chat, onClose }: { connectionId: string; chat: WhatsAppChat; onClose: () => void }) {
  const [info, setInfo] = useState<WhatsAppGroupInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const [editing, setEditing] = useState<"rename" | "description" | null>(null);
  const [draft, setDraft] = useState("");
  const [memberMenu, setMemberMenu] = useState<string | null>(null);
  const reload = useCallback(() => { void api.whatsAppGroup(connectionId, chat.chatId).then(setInfo, (e) => setError(e instanceof ApiError ? e.message : "Could not load the group.")); }, [connectionId, chat.chatId]);
  useEffect(() => { const t = setTimeout(reload, 0); return () => clearTimeout(t); }, [reload]);
  const iAmAdmin = info?.members.some((m) => m.isMe && m.isAdmin) ?? false;

  async function act(action: Parameters<typeof api.whatsAppGroupAction>[2], opts: { text?: string; participants?: string[] } = {}, done?: string) {
    setError(null); setNote(null);
    try {
      const r = await api.whatsAppGroupAction(connectionId, chat.chatId, action, opts);
      if (action === "invite" && r.link) { await navigator.clipboard?.writeText(r.link); setNote(`Invite link copied: ${r.link}`); }
      else if (done) setNote(done);
      setEditing(null); setMemberMenu(null);
      if (action === "leave") onClose(); else reload();
    } catch (e) { setError(e instanceof ApiError ? e.message : "That change did not work. Only group admins can do this."); }
  }
  const item = "block w-full rounded-lg px-3 py-1.5 text-left text-xs hover:bg-surface-2";
  return (
    <div className="border-b border-line bg-surface-2 p-3 text-sm">
      <div className="flex items-start justify-between gap-2">
        <div className="flex min-w-0 items-center gap-3"><ChatAvatar connectionId={connectionId} chatId={chat.chatId} name={chatDisplayName(chat)} isGroup size={48} /><div className="min-w-0"><p className="truncate font-bold">{info?.subject ?? chatDisplayName(chat)}</p>{info?.createdAt && <p className="text-xs text-muted">Created {new Date(info.createdAt).toLocaleDateString()}</p>}</div></div>
        <button type="button" onClick={onClose} aria-label="Close group info" className="text-muted hover:text-text">✕</button>
      </div>
      {error && <p role="alert" className="mt-2 text-xs text-danger">{error}</p>}
      {note && <p role="status" className="mt-2 break-all text-xs text-accent">{note}</p>}
      {info?.description && <p className="mt-2 whitespace-pre-wrap text-xs text-muted">{info.description}</p>}
      {info && (
        <div className="mt-2 flex flex-wrap gap-2">
          {iAmAdmin && <>
            <button type="button" onClick={() => { setEditing("rename"); setDraft(info.subject); }} className="rounded-full border border-line px-3 py-1 text-xs font-semibold hover:bg-surface">Rename</button>
            <button type="button" onClick={() => { setEditing("description"); setDraft(info.description ?? ""); }} className="rounded-full border border-line px-3 py-1 text-xs font-semibold hover:bg-surface">Edit description</button></>}
          <button type="button" onClick={() => void act("invite")} className="rounded-full border border-line px-3 py-1 text-xs font-semibold hover:bg-surface">Copy invite link</button>
          <button type="button" onClick={() => { if (window.confirm("Leave this group?")) void act("leave"); }} className="rounded-full border border-danger px-3 py-1 text-xs font-semibold text-danger hover:bg-danger-bg">Leave group</button>
        </div>
      )}
      {editing && (
        <div className="mt-2 flex gap-2"><input value={draft} onChange={(e) => setDraft(e.target.value)} aria-label={editing === "rename" ? "Group name" : "Group description"} className={inputClass} autoFocus />
          <button type="button" onClick={() => void act(editing, { text: draft }, "Saved")} className={`${buttonClass} px-4`}>Save</button></div>
      )}
      {info && (
        <>
          <p className="mt-3 text-xs font-semibold uppercase tracking-wide text-muted">{info.members.length} members</p>
          <ul className="mt-1 max-h-48 overflow-y-auto">
            {info.members.map((mem) => (
              <li key={mem.id} className="relative flex items-center gap-2 py-1">
                <ChatAvatar connectionId={connectionId} chatId={mem.id} name={mem.name ?? mem.id} size={28} />
                <span className="min-w-0 flex-1 truncate">{mem.isMe ? "You" : mem.name ?? `+${mem.id.split("@")[0]}`}</span>
                {mem.isAdmin && <span className="ai-mono rounded-full bg-lime px-2 py-0.5 text-[10px] font-semibold text-on-tint">ADMIN</span>}
                {iAmAdmin && !mem.isMe && <button type="button" onClick={() => setMemberMenu(memberMenu === mem.id ? null : mem.id)} aria-label={`Options for ${mem.name ?? mem.id}`} className="rounded-full px-2 text-muted hover:text-text">⋯</button>}
                {memberMenu === mem.id && (
                  <div role="menu" className="absolute right-0 top-full z-20 w-40 rounded-xl border border-line bg-surface p-1 shadow-[var(--shadow)]">
                    <button role="menuitem" className={item} onClick={() => void act(mem.isAdmin ? "demote" : "promote", { participants: [mem.id] }, mem.isAdmin ? "Admin removed" : "Made admin")}>{mem.isAdmin ? "Remove admin" : "Make admin"}</button>
                    <button role="menuitem" className={`${item} text-danger`} onClick={() => void act("remove", { participants: [mem.id] }, "Member removed")}>Remove from group</button>
                  </div>
                )}
              </li>
            ))}
          </ul>
        </>
      )}
    </div>
  );
}

function presenceText(p: WhatsAppPresence | null): string | null {
  if (!p) return null;
  if (p.state === "composing") return "typing…";
  if (p.state === "recording") return "recording a voice message…";
  if (p.state === "available") return "online";
  if (p.state === "unavailable" && p.lastSeen) return `last seen ${new Date(p.lastSeen * 1000).toLocaleString([], { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" })}`;
  return null;
}

export function ChatThread({ connectionId, chat, onClose, onMinimize, onSent, bodyHeightClass = "max-h-80", embedded = false }: {
  connectionId: string; chat: WhatsAppChat; onClose: () => void; onMinimize?: () => void; onSent: () => void; bodyHeightClass?: string;
  /** Inside a floating chat window: no outer border and no header of its own. */
  embedded?: boolean;
}) {
  const [messages, setMessages] = useState<WhatsAppMessage[] | null>(null);
  const [text, setText] = useState("");
  const [replyingTo, setReplyingTo] = useState<WhatsAppMessage | null>(null);
  const [editing, setEditing] = useState<WhatsAppMessage | null>(null);
  const [forwarding, setForwarding] = useState<WhatsAppMessage | null>(null);
  const [file, setFile] = useState<File | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [presence, setPresence] = useState<WhatsAppPresence | null>(null);
  const [search, setSearch] = useState<string | null>(null);
  const [showGroup, setShowGroup] = useState(false);
  const [more, setMore] = useState(false);
  const [translations, setTranslations] = useState<Record<string, string>>({});
  const scrollRef = useRef<HTMLDivElement>(null);
  const lastSeenId = useRef<string | null>(null);
  const markedRead = useRef<string | null>(null);
  const typingTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const typing = useRef(false);
  const fileInput = useRef<HTMLInputElement>(null);

  // Always land on the newest message when a chat opens, and follow along as new ones arrive.
  useEffect(() => {
    const last = messages?.[messages.length - 1]?.messageId ?? null;
    if (last === lastSeenId.current) return;
    lastSeenId.current = last;
    const el = scrollRef.current;
    if (el) requestAnimationFrame(() => el.scrollTo({ top: el.scrollHeight }));
  }, [messages]);

  // A photo or video finishing loading grows the thread; if the reader was at the bottom, stay there.
  const keepAtBottom = useCallback(() => {
    const el = scrollRef.current;
    if (el && el.scrollHeight - el.scrollTop - el.clientHeight < 450) el.scrollTo({ top: el.scrollHeight });
  }, []);

  const load = useCallback(async () => {
    try { setMessages(await api.whatsAppChatMessages(connectionId, chat.chatId)); } catch { /* best-effort */ }
  }, [connectionId, chat.chatId]);

  useEffect(() => { const timer = setTimeout(() => void load(), 0); return () => clearTimeout(timer); }, [load]);

  useEffect(() => {
    const hub = getIntegrationsHub();
    const onMessage = (payload: { connectionId: string; chatId?: string }) => { if (payload.connectionId === connectionId && payload.chatId === chat.chatId) void load(); };
    // Covers an incoming message, anything sent from here (manual reply, auto-reply, another open tab), a reaction, a delete, an edit and a tick change.
    hub.on("WhatsAppMessageReceived", onMessage);
    hub.on("WhatsAppMessageSent", onMessage);
    hub.on("WhatsAppMessageUpdated", onMessage);
    return () => { hub.off("WhatsAppMessageReceived", onMessage); hub.off("WhatsAppMessageSent", onMessage); hub.off("WhatsAppMessageUpdated", onMessage); };
  }, [connectionId, chat.chatId, load]);

  // SignalR should deliver this instantly, but a dropped/stuck connection has repeatedly left this thread stale until a manual
  // refresh, so an open thread also polls.
  useEffect(() => { const interval = setInterval(() => void load(), 4000); return () => clearInterval(interval); }, [load]);

  // Typing / online status of the other person.
  useEffect(() => {
    let live = true;
    const tick = () => { void api.whatsAppPresence(connectionId, chat.chatId).then((p) => { if (live) setPresence(p); }, () => undefined); };
    tick();
    const interval = setInterval(tick, 3000);
    return () => { live = false; clearInterval(interval); };
  }, [connectionId, chat.chatId]);

  // Opening a chat tells WhatsApp its messages were read (the sender's blue ticks), once per newest incoming message.
  useEffect(() => {
    if (!messages || typeof document === "undefined" || document.visibilityState !== "visible") return;
    const newestIncoming = [...messages].reverse().find((m) => !m.fromMe && !m.deleted)?.messageId ?? null;
    if (!newestIncoming || markedRead.current === newestIncoming) return;
    markedRead.current = newestIncoming;
    void api.markWhatsAppChatRead(connectionId, chat.chatId).catch(() => undefined);
  }, [messages, connectionId, chat.chatId]);

  function stopTyping() {
    if (typingTimer.current) clearTimeout(typingTimer.current);
    if (typing.current) { typing.current = false; void api.setWhatsAppPresence(connectionId, chat.chatId, "paused").catch(() => undefined); }
  }
  useEffect(() => () => { if (typingTimer.current) clearTimeout(typingTimer.current); }, []);
  function onType(value: string) {
    setText(value);
    if (!value) { stopTyping(); return; }
    if (!typing.current) { typing.current = true; void api.setWhatsAppPresence(connectionId, chat.chatId, "composing").catch(() => undefined); }
    if (typingTimer.current) clearTimeout(typingTimer.current);
    typingTimer.current = setTimeout(stopTyping, 4000);
  }

  const fail = (err: unknown, fallback: string) => setError(err instanceof ApiError ? err.message : fallback);

  async function send() {
    if (busy) return;
    if (!file && !text.trim()) return;
    setError(null); setBusy(true); stopTyping();
    try {
      if (editing) { await api.editWhatsAppMessage(connectionId, editing.messageId, text.trim()); setEditing(null); }
      else if (file) { await api.sendWhatsAppFile(connectionId, chat.chatId, file, { caption: text.trim() || undefined, quotedMessageId: replyingTo?.messageId }); setFile(null); }
      else await api.sendWhatsAppChatMessage(connectionId, chat.chatId, text, replyingTo?.messageId);
      setText(""); setReplyingTo(null);
      await load(); onSent();
    } catch (err) { fail(err, file ? "Could not send the file." : "Could not send the message."); }
    finally { setBusy(false); }
  }

  async function run(action: () => Promise<unknown>, failure: string, done?: string) {
    setError(null);
    try { await action(); if (done) { setNotice(done); setTimeout(() => setNotice(null), 2500); } await load(); onSent(); }
    catch (err) { fail(err, failure); }
  }

  async function translate(m: WhatsAppMessage) {
    setError(null);
    try { const r = await api.chatAssist("whatsapp", connectionId, chat.chatId, "translate", { messageId: m.messageId, language: typeof navigator !== "undefined" ? navigator.language : "English" }); if (r.text) setTranslations((t) => ({ ...t, [m.messageId]: r.text! })); }
    catch (err) { fail(err, "Could not translate the message."); }
  }
  const pickFile = (f: File | undefined | null) => { if (f) { setFile(f); setEditing(null); } };
  const chatAction = (fn: () => Promise<unknown>, failure: string, done: string) => { setMore(false); void run(fn, failure, done); };

  const query = search?.trim().toLowerCase() ?? "";
  const visible = messages && query ? messages.filter((m) => m.text.toLowerCase().includes(query)) : messages;
  const status = presenceText(presence);
  const name = chatDisplayName(chat);
  const hdrBtn = "flex h-9 w-9 items-center justify-center rounded-full text-[var(--wa-meta)] transition-colors hover:bg-black/10 hover:text-[var(--wa-text)]";
  const menuItem = "block w-full rounded-lg px-3 py-1.5 text-left text-xs hover:bg-surface-2";

  return (
    <div className={`wa-chat flex min-h-0 flex-col overflow-hidden text-[var(--wa-text)] ${embedded ? "h-full" : "rounded-[1.25rem] border border-line"}`} onDragOver={(e) => { if (e.dataTransfer.types.includes("Files")) e.preventDefault(); }} onDrop={(e) => { const f = e.dataTransfer.files?.[0]; if (f) { e.preventDefault(); pickFile(f); } }}>
      <div className="flex shrink-0 items-center gap-2 bg-[var(--wa-header)] px-3 py-2">
        <ChatAvatar connectionId={connectionId} chatId={chat.chatId} name={name} isGroup={chat.isGroup} size={36} />
        <div className="min-w-0 flex-1 leading-tight">
          <p className="truncate text-[15px] font-medium">{name}</p>
          <p className="truncate text-xs text-[var(--wa-meta)]">{status ?? (chat.isGroup ? "group chat" : "")}</p>
        </div>
        <button type="button" onClick={() => setSearch(search === null ? "" : null)} aria-pressed={search !== null} aria-label="Search" title="Search" className={hdrBtn}>
          <svg aria-hidden viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><circle cx="11" cy="11" r="7" /><path d="M20 20l-3.5-3.5" /></svg>
        </button>
        <span className="relative">
          <button type="button" onClick={() => setMore((v) => !v)} aria-expanded={more} aria-label="More chat options" title="More" className={hdrBtn}>
            <svg aria-hidden viewBox="0 0 24 24" width="18" height="18" fill="currentColor"><circle cx="12" cy="5" r="1.8" /><circle cx="12" cy="12" r="1.8" /><circle cx="12" cy="19" r="1.8" /></svg>
          </button>
          {more && (<>
            <button type="button" aria-label="Close menu" onClick={() => setMore(false)} className="fixed inset-0 z-10 cursor-default" />
            <div role="menu" className="absolute right-0 top-full z-20 mt-1 w-56 rounded-xl border border-line bg-surface p-1 text-text shadow-[var(--shadow)]">
              {chat.isGroup && <button role="menuitem" className={menuItem} onClick={() => { setMore(false); setShowGroup((v) => !v); }}>Group info</button>}
              <p className="px-3 pt-1 text-[10px] font-semibold uppercase tracking-wide text-muted">Disappearing messages</p>
              {([["Off", 0], ["24 hours", 86400], ["7 days", 604800], ["90 days", 7776000]] as const).map(([label, secs]) => (
                <button key={label} role="menuitem" className={menuItem} onClick={() => chatAction(() => api.setWhatsAppDisappearing(connectionId, chat.chatId, secs), "Could not change disappearing messages.", `Disappearing messages: ${label}`)}>{label}</button>
              ))}
              <hr className="my-1 border-line" />
              <button role="menuitem" className={menuItem} onClick={() => { setMore(false); exportChat(chatDisplayName(chat), messages ?? []); }}>Export chat</button>
              <button role="menuitem" className={`${menuItem} text-danger`} onClick={() => chatAction(() => api.clearWhatsAppChat(connectionId, chat.chatId), "Could not clear the chat.", "Chat cleared in Daksha")}>Clear chat (Daksha only)</button>
              {!chat.isGroup && <>
                <button role="menuitem" className={`${menuItem} text-danger`} onClick={() => chatAction(() => api.blockWhatsAppChat(connectionId, chat.chatId, true), "Could not block this contact.", "Contact blocked")}>Block contact</button>
                <button role="menuitem" className={menuItem} onClick={() => chatAction(() => api.blockWhatsAppChat(connectionId, chat.chatId, false), "Could not unblock this contact.", "Contact unblocked")}>Unblock contact</button></>}
            </div>
          </>)}
        </span>
        {onMinimize && <button type="button" onClick={onMinimize} aria-label="Minimise" title="Minimise" className={hdrBtn}>–</button>}
        <button type="button" onClick={onClose} aria-label="Close chat" title="Close" className={hdrBtn}>✕</button>
      </div>
      {showGroup && chat.isGroup && <GroupInfoPanel connectionId={connectionId} chat={chat} onClose={() => setShowGroup(false)} />}
      {search !== null && <div className="shrink-0 bg-[var(--wa-header)] px-3 pb-2"><input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Search in this chat…" aria-label="Search in this chat" className="field-in-pill w-full rounded-full bg-[var(--wa-input)] px-4 py-2 text-sm text-[var(--wa-text)] placeholder:text-[var(--wa-meta)]" autoFocus /></div>}
      <div ref={scrollRef} className={`wa-wall ${embedded ? "min-h-0 flex-1" : bodyHeightClass} space-y-1 overflow-y-auto px-3 py-2`}>
        {!visible ? <p className="text-sm text-[var(--wa-meta)]">Loading…</p>
          : visible.length === 0 ? <p className="text-sm text-[var(--wa-meta)]">{query ? "No messages match." : "No messages yet."}</p>
          : visible.map((m, i) => (
            <div key={m.messageId} className="contents">
              {(i === 0 || !sameDay(visible[i - 1].timestamp, m.timestamp)) && <DayDivider iso={m.timestamp} />}
              <MessageBubble m={m} chat={chat} connectionId={connectionId} onMediaLoaded={keepAtBottom}
                onReply={() => setReplyingTo(m)}
                onReact={(emoji) => void run(() => api.reactToWhatsAppMessage(connectionId, m.messageId, emoji), "Could not react to the message.")}
                onForward={() => setForwarding(m)}
                onEdit={() => { setEditing(m); setReplyingTo(null); setText(m.text); }}
                onDelete={(forEveryone) => void run(() => api.deleteWhatsAppMessage(connectionId, m.messageId, forEveryone), "Could not delete the message.")}
                onStar={() => void run(() => api.starWhatsAppMessage(connectionId, m.messageId, !m.starred), "Could not star the message.")}
                onTranslate={() => void translate(m)} translation={translations[m.messageId]} />
            </div>
          ))}
      </div>
      {(replyingTo || editing || file) && (
        <div className="flex shrink-0 items-center justify-between gap-2 bg-[var(--wa-header)] px-3 py-2 text-xs">
          <span className="truncate rounded-md border-l-4 border-[var(--wa-accent)] bg-[var(--wa-quote-in)] px-2 py-1 text-[var(--wa-meta)]">
            {editing ? <>Editing: {editing.text}</> : file ? <>📎 {file.name} ({fileSize(file.size)})</> : <>Replying to: {replyingTo?.text}</>}
          </span>
          <button onClick={() => { setReplyingTo(null); setEditing(null); setFile(null); if (editing) setText(""); }} aria-label="Cancel" className="shrink-0 text-[var(--wa-meta)] hover:text-[var(--wa-text)]">✕</button>
        </div>
      )}
      <div className="bg-[var(--wa-header)]"><AssistBar compact={embedded} channel="whatsapp" connectionId={connectionId} chatId={chat.chatId} onUse={(t) => { setText(t); setEditing(null); }} /></div>
      <div className="flex shrink-0 items-end gap-2 bg-[var(--wa-header)] px-2 py-2">
        <input ref={fileInput} type="file" className="sr-only" aria-label="Attach a file" onChange={(e) => { const f = e.target.files?.[0]; if (f) { setFile(f); setEditing(null); } e.target.value = ""; }} />
        <div className="flex min-w-0 flex-1 items-center rounded-3xl bg-[var(--wa-input)] px-1.5">
          <EmojiButton plain disabled={busy} onPick={(e) => onType(text + e)} />
          <input value={text} onChange={(e) => onType(e.target.value)} onBlur={stopTyping} onPaste={(e) => { const f = e.clipboardData.files?.[0]; if (f) { e.preventDefault(); pickFile(f); } }} onKeyDown={(e) => { if (e.key === "Enter") void send(); }}
            placeholder={file ? "Add a caption…" : editing ? "Edit your message…" : "Type a message"} className="field-in-pill min-w-0 flex-1 bg-transparent px-1 py-2.5 text-[14px] text-[var(--wa-text)] placeholder:text-[var(--wa-meta)]" />
          <button type="button" onClick={() => fileInput.current?.click()} disabled={busy || editing !== null} aria-label="Attach a photo, video, voice note or file" title="Attach" className="rounded-full p-2 text-[var(--wa-meta)] hover:text-[var(--wa-text)] disabled:opacity-50">
            <svg aria-hidden viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="M20.5 11.5l-8.2 8.2a5 5 0 0 1-7.1-7.1l8.5-8.5a3.3 3.3 0 0 1 4.7 4.7l-8.5 8.5a1.7 1.7 0 0 1-2.4-2.4l7.8-7.8" /></svg>
          </button>
        </div>
        <button onClick={send} disabled={busy || (!file && !text.trim())} aria-label={editing ? "Save" : "Send"} title={editing ? "Save" : "Send"} className="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-[var(--wa-accent)] text-white transition-opacity disabled:opacity-60">
          {busy ? "…" : editing ? "✓" : <svg aria-hidden viewBox="0 0 24 24" width="20" height="20" fill="currentColor"><path d="M3.4 20.4l17.4-7.5a1 1 0 0 0 0-1.8L3.4 3.6a.9.9 0 0 0-1.3 1l2 6.1 8.9 1.3-8.9 1.3-2 6.1a.9.9 0 0 0 1.3 1z" /></svg>}
        </button>
      </div>
      {notice && <p role="status" className="bg-[var(--wa-header)] px-3 pb-2 text-xs text-[var(--wa-accent)]">{notice}</p>}
      {error && <p role="alert" className="bg-[var(--wa-header)] px-3 pb-3 text-sm text-danger">{error}</p>}
      {forwarding && <ForwardDialog connectionId={connectionId} onClose={() => setForwarding(null)} onPick={(target) => { const m = forwarding; setForwarding(null); void run(() => api.forwardWhatsAppMessage(connectionId, m.messageId, target.chatId), "Could not forward the message.", `Forwarded to ${chatDisplayName(target)}`); }} />}
    </div>
  );
}
