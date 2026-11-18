"use client";
import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { api, ApiError, type GmailMessage, type GmailMessageSummary } from "@/lib/api";
import { GmailMessageBody } from "@/components/GmailMessageBody";
import { gmailControl } from "@/lib/gmailControl";
import { inputClass, Loading, tileClass } from "@/components/ui";

const FOLDERS: { id: string; label: string; query: string; icon: string }[] = [
  { id: "inbox", label: "Inbox", query: "in:inbox", icon: "M3 13h5l1 3h6l1-3h5M5 5h14l2 8v6H3v-6z" },
  { id: "starred", label: "Starred", query: "is:starred", icon: "M12 3l2.8 5.7 6.2.9-4.5 4.4 1.1 6.2L12 17.2 6.4 20.2l1.1-6.2L3 9.6l6.2-.9z" },
  { id: "sent", label: "Sent", query: "in:sent", icon: "M3 11l18-8-8 18-2-8z" },
  { id: "drafts", label: "Drafts", query: "in:drafts", icon: "M5 4h14v16H5zM8 9h8M8 13h8" },
  { id: "spam", label: "Spam", query: "in:spam", icon: "M12 3l9 16H3zM12 10v4M12 17v.5" },
  { id: "trash", label: "Trash", query: "in:trash", icon: "M5 7h14M9 7V4h6v3M7 7l1 13h8l1-13" },
  { id: "all", label: "All Mail", query: "in:anywhere", icon: "M4 6h16M4 12h16M4 18h16" },
];

const AVATAR_COLORS = ["#1a73e8", "#d93025", "#188038", "#e37400", "#9334e6", "#007b83", "#c5221f", "#5f6368"];

function messageError(error: unknown, fallback: string) {
  return error instanceof ApiError ? error.message : fallback;
}

/** `"Jane Doe" <jane@x.com>` → name and address. */
export function parseSender(from: string) {
  const match = from.match(/^\s*"?([^"<]*?)"?\s*<([^>]+)>\s*$/);
  const address = (match ? match[2] : from).trim();
  const name = (match ? match[1] : "").trim() || address.split("@")[0] || "(unknown sender)";
  return { name, address };
}

function avatarColor(seed: string) {
  let hash = 0;
  for (const ch of seed) hash = (hash * 31 + ch.charCodeAt(0)) >>> 0;
  return AVATAR_COLORS[hash % AVATAR_COLORS.length];
}

export function Avatar({ name, size = 36 }: { name: string; size?: number }) {
  const letter = (name.trim()[0] ?? "?").toUpperCase();
  return (
    <span
      aria-hidden
      className="inline-flex shrink-0 items-center justify-center rounded-full font-semibold text-white"
      style={{ width: size, height: size, background: avatarColor(name), fontSize: size * 0.45 }}
    >
      {letter}
    </span>
  );
}

/** Gmail-style date: today → time, this year → "Nov 18", older → "11/18/24". */
export function listDate(value: string | null) {
  if (!value) return "";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "";
  const now = new Date();
  if (date.toDateString() === now.toDateString()) return date.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
  if (date.getFullYear() === now.getFullYear()) return date.toLocaleDateString([], { month: "short", day: "numeric" });
  return date.toLocaleDateString([], { year: "2-digit", month: "numeric", day: "numeric" });
}

function fullDate(value: string) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "" : date.toLocaleString([], { weekday: "short", month: "short", day: "numeric", year: "numeric", hour: "numeric", minute: "2-digit" });
}

function Icon({ path, className = "h-5 w-5" }: { path: string; className?: string }) {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" className={className} aria-hidden>
      <path d={path} />
    </svg>
  );
}

type Draft = { to: string; subject: string; body: string };

export function GmailMailbox({ showImages, onOpenSettings, initial }: {
  showImages: boolean; onOpenSettings?: () => void;
  /** Where to start when opened from the dashboard card: a query, a message to open, a blank compose window, or a focused search box. */
  initial?: { query?: string; compose?: boolean; focusSearch?: boolean; openMessageId?: string };
}) {
  const startQuery = initial?.query ?? "in:inbox";
  const [folder, setFolder] = useState(FOLDERS.find((f) => f.query === startQuery)?.id ?? "");
  const [query, setQuery] = useState(startQuery);
  const [messages, setMessages] = useState<GmailMessageSummary[]>([]);
  const [nextPageToken, setNextPageToken] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [selected, setSelected] = useState<GmailMessage | null>(null);
  const [openingId, setOpeningId] = useState<string | null>(null);
  const [draft, setDraft] = useState<Draft | null>(initial?.compose ? { to: "", subject: "", body: "" } : null);
  const [sending, setSending] = useState(false);
  const request = useRef(0);

  const load = useCallback(async (search: string, pageToken?: string | null, append = false) => {
    const ticket = ++request.current;
    setError(null);
    setLoading(true);
    try {
      const result = await api.gmailMessages(search, pageToken);
      if (ticket !== request.current) return;
      setMessages((previous) => (append ? [...previous, ...result.messages] : result.messages));
      setNextPageToken(result.nextPageToken);
    } catch (err) {
      if (ticket === request.current) setError(messageError(err, "Could not load Gmail messages."));
    } finally {
      if (ticket === request.current) setLoading(false);
    }
  }, []);

  useEffect(() => { void load(startQuery); }, [load, startQuery]);
  const openId = initial?.openMessageId;
  useEffect(() => {
    if (!openId) return;
    api.gmailMessage(openId).then(setSelected, (err) => setError(messageError(err, "Could not open this message.")));
  }, [openId]);

  function chooseFolder(id: string) {
    const target = FOLDERS.find((f) => f.id === id)!;
    setFolder(id);
    setQuery(target.query);
    setSelected(null);
    void load(target.query);
  }

  function search(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const text = query.trim() || "in:inbox";
    setFolder(FOLDERS.find((f) => f.query === text)?.id ?? "");
    setSelected(null);
    void load(text);
  }

  /** Archive / trash / star / mark unread. The API enforces the permission profile and Google scope; if more access is needed we say so. */
  async function messageAction(action: "archive" | "trash" | "star" | "unstar" | "unread", done: string) {
    if (!selected) return;
    setError(null);
    try {
      await gmailControl.messageAction(selected.id, action);
      setNotice(done);
      if (action === "archive" || action === "trash") {
        setMessages((list) => list.filter((m) => m.id !== selected.id));
        setSelected(null);
      } else if (action === "unread") {
        setMessages((list) => list.map((m) => (m.id === selected.id ? { ...m, isUnread: true } : m)));
        setSelected(null);
      } else {
        setMessages((list) => list.map((m) => (m.id === selected.id ? { ...m, isStarred: action === "star" } : m)));
      }
    } catch (err) {
      if (err instanceof ApiError && (err.code === "scope_upgrade_required" || err.code === "permission_denied")) {
        setError(`${err.message} Open Settings (the gear) → Security to allow it.`);
      } else setError(messageError(err, "Could not update this message."));
    }
  }

  async function openMessage(message: GmailMessageSummary) {
    setOpeningId(message.id);
    setError(null);
    try {
      setSelected(await api.gmailMessage(message.id));
      setMessages((list) => list.map((m) => (m.id === message.id ? { ...m, isUnread: false } : m)));
    } catch (err) {
      setError(messageError(err, "Could not open this message."));
    } finally {
      setOpeningId(null);
    }
  }

  function reply(message: GmailMessage) {
    const sender = parseSender(message.from);
    const quoted = message.body.split("\n").map((line) => `> ${line}`).join("\n");
    setDraft({
      to: sender.address,
      subject: /^re:/i.test(message.subject) ? message.subject : `Re: ${message.subject}`,
      body: `\n\nOn ${fullDate(message.date)}, ${sender.name} wrote:\n${quoted}`,
    });
  }

  async function send(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!draft) return;
    setSending(true);
    setError(null);
    try {
      await api.sendGmailMessage(draft);
      setDraft(null);
      setNotice("Message sent.");
      window.setTimeout(() => setNotice(null), 4000);
    } catch (err) {
      setError(messageError(err, "Could not send this message."));
    } finally {
      setSending(false);
    }
  }

  const currentLabel = FOLDERS.find((f) => f.id === folder)?.label ?? "Search results";

  return (
    <div className={`overflow-hidden ${tileClass}`}>
      {/* Top bar: search like Gmail */}
      <div className="flex flex-wrap items-center gap-3 border-b border-line px-4 py-3">
        <span className="hidden items-center gap-2 text-lg font-semibold sm:flex">
          <Icon path="M3 6h18v12H3zM3 7l9 7 9-7" className="h-6 w-6 text-[#d93025]" /> Gmail
        </span>
        <form onSubmit={search} className="flex min-w-[14rem] flex-1 items-center gap-2 rounded-full bg-surface-2 px-4 py-1.5">
          <Icon path="M11 4a7 7 0 105 12l4 4M11 4a7 7 0 010 14" className="h-5 w-5 text-muted" />
          <label htmlFor="gmail-query" className="sr-only">Search Gmail</label>
          <input
            id="gmail-query"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Search mail"
            autoFocus={initial?.focusSearch}
            className="w-full bg-transparent py-1.5 text-sm outline-none placeholder:text-muted"
          />
          <button type="submit" disabled={loading} className="rounded-full px-3 py-1 text-xs font-semibold text-accent hover:bg-surface">Search</button>
        </form>
        {onOpenSettings && (
          <button onClick={onOpenSettings} aria-label="Gmail settings" className="rounded-full p-2 text-muted hover:bg-surface-2 hover:text-text">
            <Icon path="M12 15a3 3 0 100-6 3 3 0 000 6zM19 12l2-1-2-4-2 .5-1.5-1L15 4h-4l-.5 2.5-1.5 1L7 7l-2 4 2 1v2l-2 1 2 4 2-.5 1.5 1L11 20h4l.5-2.5 1.5-1 2 .5 2-4-2-1z" />
          </button>
        )}
      </div>

      {notice && <p role="status" className="border-b border-line bg-lime px-4 py-2 text-sm text-on-tint">{notice}</p>}
      {error && <p role="alert" className="border-b border-line bg-danger-bg px-4 py-2 text-sm text-danger">{error}</p>}

      <div className="grid min-h-[32rem] md:grid-cols-[13rem_minmax(0,1fr)]">
        {/* Sidebar */}
        <aside className="border-b border-line p-3 md:border-b-0 md:border-r">
          <button
            onClick={() => setDraft({ to: "", subject: "", body: "" })}
            className="mb-3 flex w-full items-center gap-3 rounded-2xl bg-[#c2e7ff] px-5 py-3.5 text-sm font-semibold text-[#001d35] shadow-sm transition-shadow hover:shadow-md"
          >
            <Icon path="M4 20l4-1L19 8l-3-3L5 16zM14 7l3 3" /> Compose
          </button>
          <nav aria-label="Mail folders" className="flex gap-1 overflow-x-auto md:flex-col">
            {FOLDERS.map((f) => (
              <button
                key={f.id}
                onClick={() => chooseFolder(f.id)}
                aria-current={folder === f.id ? "page" : undefined}
                className={`flex shrink-0 items-center gap-3 rounded-full px-4 py-2 text-left text-sm transition-colors ${folder === f.id ? "bg-[#d3e3fd] font-bold text-[#041e49]" : "hover:bg-surface-2"}`}
              >
                <Icon path={f.icon} className="h-4.5 w-4.5" /> {f.label}
              </button>
            ))}
          </nav>
        </aside>

        {/* Main pane: message list or reading view */}
        <section className="min-w-0" aria-label="Gmail messages">
          {selected ? (
            <article className="p-4 sm:p-6">
              <div className="mb-4 flex items-center gap-2">
                <button onClick={() => setSelected(null)} aria-label={`Back to ${currentLabel}`} className="rounded-full p-2 hover:bg-surface-2">
                  <Icon path="M15 5l-7 7 7 7" />
                </button>
                <span className="ml-auto flex items-center gap-1" role="toolbar" aria-label="Message actions">
                  <button onClick={() => messageAction("archive", "Conversation archived.")} className="rounded-full px-3 py-1.5 text-xs font-semibold hover:bg-surface-2">Archive</button>
                  <button onClick={() => messageAction("trash", "Moved to trash.")} className="rounded-full px-3 py-1.5 text-xs font-semibold hover:bg-surface-2">Trash</button>
                  <button onClick={() => messageAction("unread", "Marked as unread.")} className="rounded-full px-3 py-1.5 text-xs font-semibold hover:bg-surface-2">Mark unread</button>
                  <button onClick={() => messageAction(messages.find((m) => m.id === selected.id)?.isStarred ? "unstar" : "star", "Star updated.")} className="rounded-full px-3 py-1.5 text-xs font-semibold hover:bg-surface-2">Star</button>
                </span>
                <button onClick={() => reply(selected)} className="flex items-center gap-2 rounded-full border border-line px-4 py-1.5 text-sm font-semibold hover:bg-surface-2">
                  <Icon path="M9 8L4 13l5 5M4 13h10a6 6 0 016 6" className="h-4 w-4" /> Reply
                </button>
              </div>
              <h2 className="text-[1.375rem] font-normal leading-snug">{selected.subject || "(no subject)"}</h2>
              <div className="mt-4 flex items-start gap-3">
                <Avatar name={parseSender(selected.from).name} size={40} />
                <div className="min-w-0 flex-1">
                  <p className="text-sm">
                    <span className="font-semibold">{parseSender(selected.from).name}</span>{" "}
                    <span className="text-xs text-muted">&lt;{parseSender(selected.from).address}&gt;</span>
                  </p>
                  <p className="truncate text-xs text-muted">to {selected.to || "me"}</p>
                </div>
                <time className="shrink-0 text-xs text-muted">{fullDate(selected.date)}</time>
              </div>
              <div className="mt-5 border-t border-line pt-4">
                <GmailMessageBody key={selected.id} html={selected.html} text={selected.body} showImages={showImages} />
              </div>
            </article>
          ) : (
            <>
              {loading && messages.length === 0 && <div className="p-6"><Loading label="Loading messages" /></div>}
              {!loading && !error && messages.length === 0 && <p className="p-8 text-center text-sm text-muted">No messages found.</p>}
              <ul className="divide-y divide-line">
                {messages.map((message) => {
                  const sender = parseSender(message.from);
                  const unread = Boolean(message.isUnread);
                  return (
                    <li key={message.id}>
                      <button
                        onClick={() => void openMessage(message)}
                        className={`flex w-full items-center gap-3 px-4 py-2.5 text-left transition-colors hover:bg-surface-2 ${unread ? "bg-surface" : "bg-surface-2/40"}`}
                      >
                        <Avatar name={sender.name} />
                        <span className={`w-36 shrink-0 truncate text-sm sm:w-48 ${unread ? "font-bold" : ""}`}>{sender.name}</span>
                        <span className="min-w-0 flex-1 truncate text-sm">
                          <span className={unread ? "font-bold" : ""}>{message.subject || "(no subject)"}</span>
                          <span className="text-muted"> — {message.snippet}</span>
                        </span>
                        {message.isStarred && <Icon path="M12 3l2.8 5.7 6.2.9-4.5 4.4 1.1 6.2L12 17.2 6.4 20.2l1.1-6.2L3 9.6l6.2-.9z" className="h-4 w-4 shrink-0 fill-[#f4b400] text-[#f4b400]" />}
                        <span className={`shrink-0 text-xs ${unread ? "font-bold" : "text-muted"}`}>
                          {openingId === message.id ? "Opening…" : listDate(message.date)}
                        </span>
                      </button>
                    </li>
                  );
                })}
              </ul>
              {loading && messages.length > 0 && <div className="p-4"><Loading label="Loading messages" /></div>}
              {nextPageToken && !loading && (
                <button
                  onClick={() => void load(query.trim() || "in:inbox", nextPageToken, true)}
                  className="m-3 rounded-full border border-line px-4 py-2 text-sm font-semibold hover:bg-surface-2"
                >
                  Load more
                </button>
              )}
            </>
          )}
        </section>
      </div>

      {/* Compose window, bottom-right like Gmail */}
      {draft && (
        <form
          onSubmit={send}
          className="fixed bottom-0 right-4 z-40 flex w-[min(34rem,calc(100vw-2rem))] flex-col overflow-hidden rounded-t-xl border border-line bg-surface shadow-2xl"
        >
          <div className="flex items-center justify-between bg-[#404040] px-4 py-2.5 text-sm font-medium text-white">
            <span>{draft.subject.startsWith("Re:") ? "Reply" : "New Message"}</span>
            <button type="button" onClick={() => setDraft(null)} aria-label="Close compose" className="rounded p-1 hover:bg-white/20">✕</button>
          </div>
          <input value={draft.to} onChange={(e) => setDraft({ ...draft, to: e.target.value })} required placeholder="To" aria-label="To" className={`${inputClass} !rounded-none !border-0 !border-b`} />
          <input value={draft.subject} onChange={(e) => setDraft({ ...draft, subject: e.target.value })} required placeholder="Subject" aria-label="Subject" className={`${inputClass} !rounded-none !border-0 !border-b`} />
          <textarea value={draft.body} onChange={(e) => setDraft({ ...draft, body: e.target.value })} required rows={10} aria-label="Message" className={`${inputClass} !rounded-none !border-0 resize-y`} />
          <div className="flex items-center gap-2 p-3">
            <button type="submit" disabled={sending} className="rounded-full bg-[#0b57d0] px-6 py-2 text-sm font-semibold text-white hover:bg-[#0a4cb8] disabled:opacity-60">
              {sending ? "Sending…" : "Send"}
            </button>
          </div>
        </form>
      )}
    </div>
  );
}
