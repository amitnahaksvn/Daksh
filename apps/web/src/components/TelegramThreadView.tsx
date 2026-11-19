"use client";
import { useCallback, useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { ChatAvatar } from "@/components/ChatAvatar";
import { Linkified } from "@/components/Linkified";
import { DayDivider, EmojiButton, exportChat, sameDay } from "@/components/ChatExtras";
import { MessageTicks } from "@/components/WhatsAppThread";
import { buttonClass, inputClass } from "@/components/ui";
import { api, ApiError, type TelegramChat, type TelegramGroupInfo, type TelegramMessage, type TelegramPresence } from "@/lib/api";
import { getIntegrationsHub } from "@/lib/signalr";

const REACTIONS = ["👍", "❤️", "😂", "😮", "😢", "🙏"];
const LABELS = new Set(["📷 Photo", "🎥 Video", "🎤 Voice message", "🎵 Audio", "📄 Document", "Sticker"]);
const fileSize = (bytes: number) => (bytes > 1048576 ? `${(bytes / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`);

/** Attachments need the bearer token, so they are fetched as a blob rather than used as a plain src. */
function Attachment({ connectionId, m, onLoaded }: { connectionId: string; m: TelegramMessage; onLoaded: () => void }) {
  const [src, setSrc] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    let url: string | null = null; let cancelled = false;
    api.telegramMedia(connectionId, m.chatId, m.messageId).then((blob) => { if (cancelled) return; url = URL.createObjectURL(blob); setSrc(url); }, () => { if (!cancelled) setFailed(true); });
    return () => { cancelled = true; if (url) URL.revokeObjectURL(url); };
  }, [connectionId, m.chatId, m.messageId]);
  if (failed) return null;
  const kind = m.mediaKind ?? "photo";
  if (!src) return <div className={`mb-1 animate-pulse rounded-lg bg-black/10 ${kind === "audio" || kind === "voice" ? "h-10 w-56" : kind === "document" ? "h-12 w-56" : "h-40 w-56"}`} />;
  if (kind === "video") return <video src={src} controls preload="metadata" onLoadedMetadata={onLoaded} className="mb-1 max-h-72 max-w-full rounded-lg" />;
  if (kind === "audio" || kind === "voice") return <audio src={src} controls preload="metadata" onLoadedMetadata={onLoaded} className="mb-1 w-60 max-w-full" aria-label={kind === "voice" ? "Voice message" : "Audio"} />;
  if (kind === "document") return <a href={src} download={m.fileName ?? "file"} className="mb-1 flex items-center gap-2 rounded-lg bg-black/10 px-3 py-2 text-xs font-semibold hover:bg-black/15"><span aria-hidden>📄</span><span className="min-w-0 truncate">{m.fileName ?? "Document"}</span><span className="shrink-0 opacity-70">Download</span></a>;
  // eslint-disable-next-line @next/next/no-img-element
  return <a href={src} target="_blank" rel="noreferrer"><img src={src} onLoad={onLoaded} alt={kind === "sticker" ? "Sticker" : "Telegram photo"} className={`mb-1 rounded-lg object-contain ${kind === "sticker" ? "max-h-32" : "max-h-72 max-w-full"}`} /></a>;
}

function Bubble({ m, chat, connectionId, onMediaLoaded, onReply, onReact, onForward, onEdit, onDelete, onStar, onTranslate, translation }: {
  m: TelegramMessage; chat: TelegramChat; connectionId: string; onMediaLoaded: () => void;
  onReply: () => void; onReact: (emoji: string) => void; onForward: () => void; onEdit: () => void; onDelete: (forEveryone: boolean) => void; onStar: () => void;
  onTranslate: () => void; translation?: string;
}) {
  const [menu, setMenu] = useState<"closed" | "actions" | "react" | "delete">("closed");
  const close = () => setMenu("closed");
  const item = "block w-full rounded-lg px-3 py-1.5 text-left text-xs hover:bg-surface-2";
  if (m.deleted) return <div className={`max-w-[85%] rounded-xl border border-dashed border-line px-3 py-2 text-sm italic text-muted ${m.fromMe ? "self-end" : "self-start"}`}>🚫 {m.fromMe ? "You deleted this message" : "This message was deleted"}</div>;
  const showText = !(m.hasMedia && LABELS.has(m.text));
  const canEdit = m.fromMe && !m.mediaKind;
  return (
    <div className={`relative max-w-[85%] rounded-2xl px-3.5 py-2 text-sm ${m.fromMe ? "self-end bg-accent text-accent-contrast" : "self-start bg-surface-2"}`}>
      {!m.fromMe && m.senderName && chat.isGroup && <p className="text-[11px] font-semibold text-muted">{m.senderName}</p>}
      {m.replyToText && <div className={`mb-1 truncate rounded-lg border-l-2 px-2 py-1 text-xs opacity-80 ${m.fromMe ? "border-accent-contrast/50 bg-black/10" : "border-line bg-surface"}`}>{m.replyToText}</div>}
      {m.hasMedia && <Attachment connectionId={connectionId} m={m} onLoaded={onMediaLoaded} />}
      {showText && <p className="whitespace-pre-wrap break-words"><Linkified text={m.text} /></p>}
      {translation && <p className="mt-1 whitespace-pre-wrap break-words rounded-lg bg-black/10 px-2 py-1 text-xs"><span aria-hidden>🌐 </span>{translation}</p>}
      {!!m.reactions?.length && <div className="mt-1 flex flex-wrap gap-1">{m.reactions.map((r) => <span key={r.emoji} className={`rounded-full px-1.5 py-0.5 text-xs ${r.mine ? "bg-black/25" : "bg-black/10"}`}>{r.emoji}{r.count > 1 ? ` ${r.count}` : ""}</span>)}</div>}
      <div className="mt-0.5 flex items-center justify-end gap-2 text-[10px] opacity-80">
        {m.starred && <span aria-label="Starred" title="Starred">★</span>}
        {m.edited && <span className="italic">edited</span>}
        {m.isAiGenerated && <span>🤖 Daksha</span>}
        <span>{new Date(m.timestamp).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</span>
        {m.fromMe && <MessageTicks status={m.status ?? null} />}
        <button type="button" onClick={() => setMenu(menu === "closed" ? "actions" : "closed")} aria-label="Message options" aria-expanded={menu !== "closed"} className="rounded px-1 text-xs opacity-60 hover:opacity-100">⋯</button>
      </div>
      {menu !== "closed" && (
        <>
          <button type="button" aria-label="Close menu" onClick={close} className="fixed inset-0 z-10 cursor-default" />
          <div role="menu" className={`absolute z-20 w-44 rounded-xl border border-line bg-surface p-1 text-text shadow-[var(--shadow)] ${m.fromMe ? "right-0" : "left-0"} top-full mt-1`}>
            {menu === "actions" && <>
              <button role="menuitem" className={item} onClick={() => setMenu("react")}>React</button>
              <button role="menuitem" className={item} onClick={() => { onReply(); close(); }}>Reply</button>
              <button role="menuitem" className={item} onClick={() => { void navigator.clipboard?.writeText(m.text); close(); }}>Copy</button>
              <button role="menuitem" className={item} onClick={() => { onForward(); close(); }}>Forward</button>
              {m.text && !m.mediaKind && <button role="menuitem" className={item} onClick={() => { onTranslate(); close(); }}>Translate</button>}
              <button role="menuitem" className={item} onClick={() => { onStar(); close(); }}>{m.starred ? "Unstar" : "Star"}</button>
              {canEdit && <button role="menuitem" className={item} onClick={() => { onEdit(); close(); }}>Edit</button>}
              <button role="menuitem" className={`${item} text-danger`} onClick={() => setMenu("delete")}>Delete…</button>
            </>}
            {menu === "react" && <div className="flex flex-wrap gap-1 p-1">{REACTIONS.map((e) => <button key={e} type="button" onClick={() => { onReact(e); close(); }} className="text-lg transition-transform hover:scale-125">{e}</button>)}</div>}
            {menu === "delete" && <>
              <p className="px-3 py-1 text-[11px] text-muted">Delete this message?</p>
              <button role="menuitem" className={`${item} text-danger`} onClick={() => { onDelete(true); close(); }}>Delete for everyone</button>
              <button role="menuitem" className={`${item} text-danger`} onClick={() => { onDelete(false); close(); }}>Delete for me</button>
              <button role="menuitem" className={item} onClick={close}>Cancel</button>
            </>}
          </div>
        </>
      )}
    </div>
  );
}

function ForwardDialog({ connectionId, onPick, onClose }: { connectionId: string; onPick: (c: TelegramChat) => void; onClose: () => void }) {
  const [chats, setChats] = useState<TelegramChat[] | null>(null);
  const [q, setQ] = useState("");
  useEffect(() => { void api.telegramChats(connectionId).then(setChats, () => setChats([])); }, [connectionId]);
  const shown = (chats ?? []).filter((c) => c.name.toLowerCase().includes(q.trim().toLowerCase()));
  return createPortal(
    <div role="dialog" aria-modal="true" aria-label="Forward to" className="fixed inset-0 z-[1000] flex items-end justify-center bg-black/50 sm:items-center sm:p-6" onClick={onClose}>
      <div className="max-h-[80vh] w-full max-w-md overflow-y-auto rounded-t-3xl bg-canvas p-4 text-text shadow-2xl sm:rounded-3xl" onClick={(e) => e.stopPropagation()}>
        <div className="mb-3 flex items-center justify-between"><h2 className="text-lg font-bold">Forward to</h2><button type="button" onClick={onClose} className="text-sm text-muted">Cancel</button></div>
        <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search chats…" className={`${inputClass} mb-2`} autoFocus />
        {!chats ? <p className="text-sm text-muted">Loading…</p> : shown.length === 0 ? <p className="text-sm text-muted">No chats found.</p> : <ul>{shown.map((c) => (
          <li key={c.chatId}><button type="button" onClick={() => onPick(c)} className="flex w-full items-center gap-3 rounded-xl px-2 py-2 text-left hover:bg-surface-2"><ChatAvatar channel="telegram" connectionId={connectionId} chatId={c.chatId} name={c.name} isGroup={c.isGroup} size={36} /><span className="min-w-0 flex-1 truncate text-sm font-semibold">{c.name}</span></button></li>
        ))}</ul>}
      </div>
    </div>, document.body);
}

function GroupPanel({ connectionId, chat, onClose }: { connectionId: string; chat: TelegramChat; onClose: () => void }) {
  const [info, setInfo] = useState<TelegramGroupInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { void api.telegramGroup(connectionId, chat.chatId).then(setInfo, (e) => setError(e instanceof ApiError ? e.message : "Could not load the group.")); }, [connectionId, chat.chatId]);
  return (
    <div className="border-b border-line bg-surface-2 p-3 text-sm">
      <div className="flex items-start justify-between gap-2"><p className="truncate font-bold">{info?.subject ?? chat.name}</p><button type="button" onClick={onClose} aria-label="Close group info" className="text-muted hover:text-text">✕</button></div>
      {error && <p role="alert" className="mt-2 text-xs text-danger">{error}</p>}
      {info?.description && <p className="mt-2 whitespace-pre-wrap text-xs text-muted">{info.description}</p>}
      {info && <><p className="mt-3 text-xs font-semibold uppercase tracking-wide text-muted">{info.members.length} members</p>
        <ul className="mt-1 max-h-48 overflow-y-auto">{info.members.map((mem) => (
          <li key={mem.id} className="flex items-center gap-2 py-1"><ChatAvatar channel="telegram" connectionId={connectionId} chatId={mem.id} name={mem.name} size={28} /><span className="min-w-0 flex-1 truncate">{mem.name}</span>{mem.isAdmin && <span className="ai-mono rounded-full bg-lime px-2 py-0.5 text-[10px] font-semibold text-on-tint">ADMIN</span>}</li>
        ))}</ul></>}
    </div>
  );
}

function presenceText(p: TelegramPresence | null): string | null {
  if (!p) return null;
  if (p.state === "typing") return "typing…";
  if (p.state === "recording") return "recording a voice message…";
  if (p.state === "uploading") return "sending a file…";
  if (p.state === "online") return "online";
  if (p.state === "offline" && p.lastSeen) return `last seen ${new Date(p.lastSeen * 1000).toLocaleString([], { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" })}`;
  if (p.state === "recently") return "last seen recently";
  return null;
}

const PAGE = 200;

/** One Telegram conversation with everything a messenger has: ticks, attachments, replies, reactions, edit, delete, forward, search and group details. */
export function TelegramThreadView({ connectionId, chat, onClose, onSent, bodyHeightClass = "max-h-80", embedded = false }: {
  connectionId: string; chat: TelegramChat; onClose?: () => void; onSent?: () => void; bodyHeightClass?: string; embedded?: boolean;
}) {
  const [messages, setMessages] = useState<TelegramMessage[] | null>(null);
  const [limit, setLimit] = useState(PAGE);
  const [text, setText] = useState("");
  const [replyingTo, setReplyingTo] = useState<TelegramMessage | null>(null);
  const [editing, setEditing] = useState<TelegramMessage | null>(null);
  const [forwarding, setForwarding] = useState<TelegramMessage | null>(null);
  const [file, setFile] = useState<File | null>(null);
  const [busy, setBusy] = useState(false);
  const [loadingOlder, setLoadingOlder] = useState(false);
  const [noMoreOlder, setNoMoreOlder] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [presence, setPresence] = useState<TelegramPresence | null>(null);
  const [search, setSearch] = useState<string | null>(null);
  const [showGroup, setShowGroup] = useState(false);
  const [translations, setTranslations] = useState<Record<string, string>>({});
  const scrollRef = useRef<HTMLDivElement>(null);
  const lastSeenId = useRef<string | null>(null);
  const markedRead = useRef<string | null>(null);
  const typingTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const typing = useRef(false);
  const fileInput = useRef<HTMLInputElement>(null);

  useEffect(() => {
    const last = messages?.[messages.length - 1]?.messageId ?? null;
    if (last === lastSeenId.current) return;
    lastSeenId.current = last;
    const el = scrollRef.current;
    if (el) requestAnimationFrame(() => el.scrollTo({ top: el.scrollHeight }));
  }, [messages]);
  const keepAtBottom = useCallback(() => { const el = scrollRef.current; if (el && el.scrollHeight - el.scrollTop - el.clientHeight < 450) el.scrollTo({ top: el.scrollHeight }); }, []);

  const load = useCallback(async () => { try { setMessages(await api.telegramMessages(connectionId, chat.chatId, limit)); } catch { /* best-effort */ } }, [connectionId, chat.chatId, limit]);
  useEffect(() => { const t = setTimeout(() => void load(), 0); return () => clearTimeout(t); }, [load]);
  useEffect(() => {
    const hub = getIntegrationsHub();
    const onEvent = (p: { connectionId: string; chatId?: string }) => { if (p.connectionId === connectionId && p.chatId === chat.chatId) void load(); };
    hub.on("TelegramMessageReceived", onEvent); hub.on("TelegramMessageUpdated", onEvent);
    return () => { hub.off("TelegramMessageReceived", onEvent); hub.off("TelegramMessageUpdated", onEvent); };
  }, [connectionId, chat.chatId, load]);
  useEffect(() => { const i = setInterval(() => void load(), 4000); return () => clearInterval(i); }, [load]);

  useEffect(() => {
    let live = true;
    const tick = () => { void api.telegramPresence(connectionId, chat.chatId).then((p) => { if (live) setPresence(p); }, () => undefined); };
    tick(); const i = setInterval(tick, 3000);
    return () => { live = false; clearInterval(i); };
  }, [connectionId, chat.chatId]);

  // Opening the chat tells Telegram its messages were read, once per newest incoming message.
  useEffect(() => {
    if (!messages || typeof document === "undefined" || document.visibilityState !== "visible") return;
    const newest = [...messages].reverse().find((m) => !m.fromMe && !m.deleted)?.messageId ?? null;
    if (!newest || markedRead.current === newest) return;
    markedRead.current = newest;
    void api.markTelegramChatRead(connectionId, chat.chatId).catch(() => undefined);
  }, [messages, connectionId, chat.chatId]);

  function stopTyping() { if (typingTimer.current) clearTimeout(typingTimer.current); if (typing.current) { typing.current = false; void api.telegramTyping(connectionId, chat.chatId, false).catch(() => undefined); } }
  useEffect(() => () => { if (typingTimer.current) clearTimeout(typingTimer.current); }, []);
  function onType(v: string) {
    setText(v);
    if (!v) { stopTyping(); return; }
    if (!typing.current) { typing.current = true; void api.telegramTyping(connectionId, chat.chatId, true).catch(() => undefined); }
    if (typingTimer.current) clearTimeout(typingTimer.current);
    typingTimer.current = setTimeout(stopTyping, 4000);
  }
  const fail = (e: unknown, fallback: string) => setError(e instanceof ApiError ? e.message : fallback);

  async function send() {
    if (busy || (!file && !text.trim())) return;
    setError(null); setBusy(true); stopTyping();
    try {
      if (editing) { await api.editTelegramMessage(connectionId, chat.chatId, editing.messageId, text.trim()); setEditing(null); }
      else if (file) { await api.sendTelegramFile(connectionId, chat.chatId, file, { caption: text.trim() || undefined, replyToId: replyingTo?.messageId }); setFile(null); }
      else await api.sendTelegramMessage(connectionId, chat.chatId, text.trim(), replyingTo?.messageId);
      setText(""); setReplyingTo(null);
      await load(); onSent?.();
    } catch (e) { fail(e, file ? "Could not send the file." : "Could not send the message."); }
    finally { setBusy(false); }
  }
  async function run(action: () => Promise<unknown>, failure: string, done?: string) {
    setError(null);
    try { await action(); if (done) { setNotice(done); setTimeout(() => setNotice(null), 2500); } await load(); onSent?.(); } catch (e) { fail(e, failure); }
  }
  async function loadOlder() {
    setLoadingOlder(true); setError(null);
    try {
      const { fetched } = await api.telegramLoadOlder(connectionId, chat.chatId);
      if (fetched === 0) setNoMoreOlder(true);
      setLimit((l) => l + PAGE);
    } catch (e) { fail(e, "Could not load older messages."); }
    finally { setLoadingOlder(false); }
  }

  async function translate(m: TelegramMessage) {
    setError(null);
    try { const r = await api.chatAssist("telegram", connectionId, chat.chatId, "translate", { messageId: m.messageId, language: typeof navigator !== "undefined" ? navigator.language : "English" }); if (r.text) setTranslations((t) => ({ ...t, [m.messageId]: r.text! })); }
    catch (e) { fail(e, "Could not translate the message."); }
  }
  const pickFile = (f: File | undefined | null) => { if (f) { setFile(f); setEditing(null); } };

  const query = search?.trim().toLowerCase() ?? "";
  const visible = messages && query ? messages.filter((m) => m.text.toLowerCase().includes(query)) : messages;
  const status = presenceText(presence);

  return (
    <div className={`flex min-h-0 flex-col ${embedded ? "h-full" : "rounded-[1.25rem] border border-line"}`} onDragOver={(e) => { if (e.dataTransfer.types.includes("Files")) e.preventDefault(); }} onDrop={(e) => { const f = e.dataTransfer.files?.[0]; if (f) { e.preventDefault(); pickFile(f); } }}>
      {!embedded && (
        <div className="flex items-center justify-between gap-2 border-b border-line p-3">
          <div className="flex min-w-0 items-center gap-3"><ChatAvatar channel="telegram" connectionId={connectionId} chatId={chat.chatId} name={chat.name} isGroup={chat.isGroup} size={40} />
            <div className="min-w-0"><div className="flex items-center gap-2"><span className="truncate font-semibold">{chat.name}</span>{chat.isGroup && <span className="ai-mono rounded-full bg-surface-2 px-1.5 py-0.5 text-[9px] font-semibold text-muted">GROUP</span>}</div>{status && <p className="text-xs text-accent">{status}</p>}</div></div>
          {onClose && <button onClick={onClose} className="text-sm text-muted hover:text-text">Close</button>}
        </div>
      )}
      <div className="flex items-center justify-between gap-2 border-b border-line px-3 py-1.5 text-xs text-muted">
        <span className="min-w-0 truncate">{embedded ? (status ?? "") : ""}</span>
        <span className="flex shrink-0 items-center gap-1">
          {chat.isGroup && <button type="button" onClick={() => setShowGroup((v) => !v)} aria-pressed={showGroup} className="rounded-full px-2 py-1 hover:bg-surface-2 hover:text-text">Group info</button>}
          <button type="button" onClick={() => setSearch(search === null ? "" : null)} aria-pressed={search !== null} className="rounded-full px-2 py-1 hover:bg-surface-2 hover:text-text">Search</button>
          <button type="button" onClick={() => exportChat(chat.name, messages ?? [])} className="rounded-full px-2 py-1 hover:bg-surface-2 hover:text-text">Export</button>
        </span>
      </div>
      {showGroup && chat.isGroup && <GroupPanel connectionId={connectionId} chat={chat} onClose={() => setShowGroup(false)} />}
      {search !== null && <div className="border-b border-line p-2"><input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Search in this chat…" aria-label="Search in this chat" className={inputClass} autoFocus /></div>}
      <div ref={scrollRef} className={`${bodyHeightClass} flex min-h-0 flex-1 flex-col gap-2 overflow-y-auto p-3`}>
        {!query && <button type="button" onClick={() => void loadOlder()} disabled={loadingOlder || noMoreOlder} className="mx-auto shrink-0 rounded-full border border-line px-3 py-1 text-xs font-semibold text-muted hover:text-text disabled:opacity-60">{loadingOlder ? "Loading…" : noMoreOlder ? "No older messages" : "Load older messages"}</button>}
        {!visible ? <p className="text-sm text-muted">Loading…</p>
          : visible.length === 0 ? <p className="text-sm text-muted">{query ? "No messages match." : "No messages yet."}</p>
          : visible.map((m, i) => (
            <div key={m.messageId} className="contents">
              {(i === 0 || !sameDay(visible[i - 1].timestamp, m.timestamp)) && <DayDivider iso={m.timestamp} />}
              <Bubble m={m} chat={chat} connectionId={connectionId} onMediaLoaded={keepAtBottom}
                onReply={() => setReplyingTo(m)}
                onReact={(e) => void run(() => api.reactTelegramMessage(connectionId, chat.chatId, m.messageId, e), "Could not react to the message.")}
                onForward={() => setForwarding(m)}
                onEdit={() => { setEditing(m); setReplyingTo(null); setText(m.text); }}
                onDelete={(all) => void run(() => api.deleteTelegramMessage(connectionId, chat.chatId, m.messageId, all), "Could not delete the message.")}
                onStar={() => void run(() => api.starTelegramMessage(connectionId, chat.chatId, m.messageId, !m.starred), "Could not star the message.")}
                onTranslate={() => void translate(m)} translation={translations[m.messageId]} />
            </div>
          ))}
      </div>
      {(replyingTo || editing || file) && (
        <div className="flex items-center justify-between gap-2 border-t border-line bg-surface-2 px-3 py-2 text-xs">
          <span className="truncate">{editing ? <>Editing: <span className="opacity-70">{editing.text}</span></> : file ? <>📎 {file.name} <span className="opacity-70">({fileSize(file.size)})</span></> : <>Replying to: <span className="opacity-70">{replyingTo?.text}</span></>}</span>
          <button onClick={() => { setReplyingTo(null); setEditing(null); setFile(null); if (editing) setText(""); }} aria-label="Cancel" className="shrink-0 text-muted hover:text-text">✕</button>
        </div>
      )}
      <div className="flex items-center gap-1.5 border-t border-line p-2.5">
        <input ref={fileInput} type="file" className="sr-only" aria-label="Attach a file" onChange={(e) => { const f = e.target.files?.[0]; if (f) { setFile(f); setEditing(null); } e.target.value = ""; }} />
        <button type="button" onClick={() => fileInput.current?.click()} disabled={busy || editing !== null} aria-label="Attach a photo, video, voice note or file" title="Attach" className="shrink-0 rounded-full p-2 text-lg leading-none opacity-70 hover:bg-surface-2 hover:opacity-100 disabled:opacity-40">📎</button>
        <EmojiButton plain disabled={busy} onPick={(e) => onType(text + e)} />
        <input value={text} onChange={(e) => onType(e.target.value)} onBlur={stopTyping} onPaste={(e) => { const f = e.clipboardData.files?.[0]; if (f) { e.preventDefault(); pickFile(f); } }} onKeyDown={(e) => { if (e.key === "Enter") void send(); }} placeholder={file ? "Add a caption…" : editing ? "Edit your message…" : "Write a message…"} aria-label="Message" className={`${inputClass} min-w-0 flex-1 !rounded-full`} />
        <button onClick={() => void send()} disabled={busy || (!file && !text.trim())} className={`${buttonClass} shrink-0 !px-4 !py-2`}>{busy ? "…" : editing ? "Save" : "Send"}</button>
      </div>
      {notice && <p role="status" className="px-3 pb-2 text-xs text-accent">{notice}</p>}
      {error && <p role="alert" className="px-3 pb-3 text-sm text-danger">{error}</p>}
      {forwarding && <ForwardDialog connectionId={connectionId} onClose={() => setForwarding(null)} onPick={(target) => { const m = forwarding; setForwarding(null); void run(() => api.forwardTelegramMessage(connectionId, chat.chatId, m.messageId, target.chatId), "Could not forward the message.", `Forwarded to ${target.name}`); }} />}
    </div>
  );
}
