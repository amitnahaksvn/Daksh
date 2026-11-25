"use client";
import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { api, ApiError, type SlackChannel, type SlackMessage, type SlackSearchHit, type SlackWorkspace } from "@/lib/api";
import { buttonClass, Dropdown, EmptyState, ErrorState, inputClass, Loading, secondaryButtonClass, tileClass } from "@/components/ui";

const errorText = (err: unknown, fallback: string) => (err instanceof ApiError ? err.message : fallback);

export function slackCallbackError(reason: string | null) {
  switch (reason) {
    case "consent_denied": return "Slack sign-in was cancelled.";
    case "oauth_not_configured": return "Slack sign-in is not configured on the API yet.";
    case "token_exchange": return "Slack did not accept the sign-in. Please try again.";
    case "token_missing": return "Slack did not return a personal access token. Check the app's user token scopes.";
    case "token_rotation": return "Turn off token rotation in the Slack app settings, then connect again.";
    case "slack_unavailable": return "Slack could not be reached. Please try again.";
    case "profile_lookup": return "Daksha could not read your Slack profile.";
    default: return "Slack could not be connected. Please try again.";
  }
}

/**
 * Opens Slack sign-in in a popup and resolves with "connected", "closed", or an error reason.
 * The popup lands on /apps/slack, which reports back with postMessage and closes itself. If popups are blocked the whole page is redirected.
 */
export function signInWithSlack(write = false): Promise<string> {
  const width = 560, height = 720;
  const left = Math.max(0, window.screenX + (window.outerWidth - width) / 2);
  const top = Math.max(0, window.screenY + (window.outerHeight - height) / 2);
  const popup = window.open("about:blank", "daksh-slack-oauth", `popup=yes,width=${width},height=${height},left=${left},top=${top}`);
  return new Promise((resolve, reject) => {
    let done = false;
    const finish = (fn: () => void) => { if (done) return; done = true; window.removeEventListener("message", onMessage); window.clearInterval(timer); fn(); };
    const onMessage = (event: MessageEvent) => {
      if (event.origin !== window.location.origin || event.data?.type !== "daksh-slack-oauth") return;
      finish(() => resolve(String(event.data.result ?? "")));
    };
    window.addEventListener("message", onMessage);
    const timer = window.setInterval(() => { if (popup?.closed) finish(() => resolve("closed")); }, 600);
    api.connectSlack(write).then(({ authorizationUrl }) => {
      if (popup && !popup.closed) popup.location.href = authorizationUrl;
      else finish(() => window.location.assign(authorizationUrl));
    }, (err) => { popup?.close(); finish(() => reject(err)); });
  });
}

/** Result text for a finished sign-in: null when there is nothing to tell the user. */
export function slackResultMessage(result: string): string | null {
  return result === "connected" || result === "closed" ? null : slackCallbackError(result);
}

export function SlackConnectCard({ title = "Connect your Slack workspace", already = false, initialError, onConnected }: { title?: string; already?: boolean; initialError?: string | null; onConnected?: () => void }) {
  const [full, setFull] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(initialError ?? null);

  async function connect() {
    setError(null); setBusy(true);
    try {
      const result = await signInWithSlack(full);
      const message = slackResultMessage(result);
      if (message) setError(message); else if (result === "connected") onConnected?.();
    } catch (err) { setError(errorText(err, "Could not start Slack sign-in.")); }
    finally { setBusy(false); }
  }

  return (
    <div className={`p-5 ${tileClass}`}>
      <h3 className="text-lg font-bold">{title}</h3>
      <p className="mt-1 text-sm text-muted">Sign in with Slack and Daksha can read your channels and direct messages, search them, and answer questions about them. It acts as you, so it only sees what you can see, and it never sees your Slack password.</p>
      <label className="mt-3 flex cursor-pointer items-start gap-3 rounded-xl border border-line p-3">
        <input type="checkbox" className="mt-1" checked={full} onChange={(e) => setFull(e.target.checked)} />
        <span><span className="block text-sm font-semibold">Allow Daksha to post messages</span><span className="block text-xs text-muted">Off by default: Daksha can then only read. Turn it on to reply from Daksha. You can change this later in Manage.</span></span>
      </label>
      {error && <p role="alert" className="mt-3 text-sm text-danger">{error}</p>}
      <button type="button" onClick={() => void connect()} disabled={busy} className={`${buttonClass} mt-4 px-5`}>{busy ? "Opening Slack…" : already ? "Add another workspace" : "Continue with Slack"}</button>
    </div>
  );
}

// ---- inbox -------------------------------------------------------------------------------------------

const timeText = (iso: string | null) => (iso ? new Date(iso).toLocaleString([], { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" }) : "");
const KIND_LABEL = { channel: "#", group: "👥", dm: "💬" } as const;
const FILTERS = [{ value: "all", label: "Everything" }, { value: "channels", label: "Channels" }, { value: "dms", label: "Direct messages" }];

function Thread({ channelId, ts, connectionId, canWrite, onClose }: { channelId: string; ts: string; connectionId: string; canWrite: boolean; onClose: () => void }) {
  const [messages, setMessages] = useState<SlackMessage[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(() => {
    api.slackReplies(channelId, ts, connectionId).then((p) => setMessages(p.messages), (e) => setError(errorText(e, "Could not load the thread.")));
  }, [channelId, ts, connectionId]);
  useEffect(() => { const t = setTimeout(load, 0); return () => clearTimeout(t); }, [load]);
  return (
    <div className="mt-3 rounded-xl border border-line bg-surface-2 p-3">
      <div className="flex items-center justify-between"><h4 className="text-sm font-bold">Thread</h4><button type="button" onClick={onClose} className="text-xs font-semibold text-accent hover:underline">Close</button></div>
      {error && <p role="alert" className="mt-2 text-sm text-danger">{error}</p>}
      {messages === null && !error ? <Loading label="Loading thread" /> : (
        <ul className="mt-2 space-y-2">{messages?.map((m) => (
          <li key={m.ts} className="text-sm"><span className="font-semibold">{m.userName}</span> <span className="text-xs text-muted">{timeText(m.time)}</span><p className="whitespace-pre-wrap break-words">{m.text}</p></li>
        ))}</ul>
      )}
      <Composer canWrite={canWrite} placeholder="Reply in thread" onSend={async (text) => { await api.sendSlackMessage(channelId, text, connectionId, ts); load(); }} />
    </div>
  );
}

function Composer({ canWrite, placeholder, onSend }: { canWrite: boolean; placeholder: string; onSend: (text: string) => Promise<void> }) {
  const [text, setText] = useState(""); const [busy, setBusy] = useState(false); const [error, setError] = useState<string | null>(null);
  if (!canWrite) return <p className="mt-3 text-xs text-muted">Daksha has read-only access to Slack. Turn on full access in Manage to post messages.</p>;
  async function submit(e: FormEvent) {
    e.preventDefault(); setBusy(true); setError(null);
    try { await onSend(text.trim()); setText(""); }
    catch (err) { setError(errorText(err, "Could not send the message.")); }
    finally { setBusy(false); }
  }
  return (
    <form onSubmit={(e) => void submit(e)} className="mt-3 space-y-2">
      <textarea value={text} onChange={(e) => setText(e.target.value)} rows={2} maxLength={4000} placeholder={placeholder} aria-label={placeholder} className={`${inputClass} resize-y`}
        onKeyDown={(e) => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); if (text.trim() && !busy) void submit(e as unknown as FormEvent); } }} />
      {error && <p role="alert" className="text-sm text-danger">{error}</p>}
      <button type="submit" disabled={busy || !text.trim()} className={buttonClass}>{busy ? "Sending…" : "Send"}</button>
    </form>
  );
}

function Conversation({ channel, workspace }: { channel: SlackChannel; workspace: SlackWorkspace }) {
  const [page, setPage] = useState<{ messages: SlackMessage[]; next: string | null } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [thread, setThread] = useState<string | null>(null);
  const [older, setOlder] = useState(false);
  const endRef = useRef<HTMLDivElement>(null);

  const load = useCallback(() => {
    setError(null);
    api.slackMessages(channel.id, workspace.connectionId).then((p) => setPage({ messages: p.messages, next: p.nextCursor }), (e) => { setError(errorText(e, "Could not load messages.")); setPage({ messages: [], next: null }); });
  }, [channel.id, workspace.connectionId]);
  useEffect(() => { const t = setTimeout(load, 0); return () => clearTimeout(t); }, [load]); // keyed by channel, so a new conversation starts fresh
  useEffect(() => { if (page && !older) endRef.current?.scrollIntoView({ block: "end" }); }, [page, older]);

  async function loadOlder() {
    if (!page?.next) return;
    setOlder(true);
    try { const p = await api.slackMessages(channel.id, workspace.connectionId, page.next); setPage({ messages: [...p.messages, ...page.messages], next: p.nextCursor }); }
    catch (e) { setError(errorText(e, "Could not load older messages.")); }
    finally { setOlder(false); }
  }

  return (
    <section className={`flex min-h-[28rem] min-w-0 flex-1 flex-col p-4 ${tileClass}`}>
      <header className="flex items-center gap-2"><span aria-hidden>{KIND_LABEL[channel.kind]}</span><h3 className="min-w-0 flex-1 truncate text-base font-bold">{channel.name}</h3>
        {workspace.write && <button type="button" className="text-xs font-semibold text-accent hover:underline" onClick={() => void api.markSlackRead(channel.id, workspace.connectionId).catch(() => undefined)}>Mark read</button>}
      </header>
      {channel.topic && <p className="truncate text-xs text-muted">{channel.topic}</p>}
      {error && <p role="alert" className="mt-2 text-sm text-danger">{error}</p>}
      <div className="mt-3 max-h-[50vh] min-h-0 flex-1 overflow-auto">
        {page === null ? <Loading label="Loading messages" /> : page.messages.length === 0 ? <EmptyState>No messages yet.</EmptyState> : (
          <>
            {page.next && <div className="mb-2 text-center"><button type="button" onClick={() => void loadOlder()} disabled={older} className={secondaryButtonClass}>{older ? "Loading…" : "Older messages"}</button></div>}
            <ul className="space-y-3">
              {page.messages.map((m) => (
                <li key={m.ts} className="text-sm">
                  <span className="font-semibold">{m.userName}</span> <span className="text-xs text-muted">{timeText(m.time)}</span>{m.isBot && <span className="ml-1 rounded bg-surface-2 px-1 text-[10px] font-bold uppercase text-muted">app</span>}
                  <p className="whitespace-pre-wrap break-words">{m.text || (m.files > 0 ? "" : "(no text)")}</p>
                  {m.files > 0 && <p className="text-xs text-muted">📎 {m.files} file{m.files === 1 ? "" : "s"} (open in Slack)</p>}
                  {m.replyCount > 0 && <button type="button" onClick={() => setThread(m.ts)} className="text-xs font-semibold text-accent hover:underline">{m.replyCount} repl{m.replyCount === 1 ? "y" : "ies"}</button>}
                </li>
              ))}
            </ul>
            <div ref={endRef} />
          </>
        )}
      </div>
      {thread && <Thread channelId={channel.id} ts={thread} connectionId={workspace.connectionId} canWrite={workspace.write} onClose={() => setThread(null)} />}
      <Composer canWrite={workspace.write} placeholder={`Message ${channel.kind === "channel" ? "#" : ""}${channel.name}`} onSend={async (text) => { await api.sendSlackMessage(channel.id, text, workspace.connectionId); load(); }} />
    </section>
  );
}

function SearchPanel({ workspace, onOpen }: { workspace: SlackWorkspace; onOpen: (channelId: string) => void }) {
  const [q, setQ] = useState(""); const [hits, setHits] = useState<SlackSearchHit[] | null>(null); const [error, setError] = useState<string | null>(null); const [busy, setBusy] = useState(false);
  async function search(e: FormEvent) {
    e.preventDefault(); if (!q.trim()) return;
    setBusy(true); setError(null);
    try { setHits(await api.slackSearch(q.trim(), workspace.connectionId)); }
    catch (err) { setError(errorText(err, "Could not search Slack.")); setHits([]); }
    finally { setBusy(false); }
  }
  return (
    <div>
      <form onSubmit={(e) => void search(e)} className="flex gap-2">
        <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search messages" aria-label="Search Slack" maxLength={200} className={inputClass} />
        <button type="submit" disabled={busy || !q.trim()} className={secondaryButtonClass}>{busy ? "…" : "Search"}</button>
      </form>
      {error && <p role="alert" className="mt-2 text-sm text-danger">{error}</p>}
      {hits && (hits.length === 0 ? <p className="mt-2 text-xs text-muted">No messages found.</p> : (
        <ul className="mt-2 divide-y divide-line rounded-xl border border-line">
          {hits.map((h) => (
            <li key={`${h.channelId}-${h.ts}`} className="px-3 py-2 text-sm">
              <button type="button" onClick={() => onOpen(h.channelId)} className="block w-full text-left"><span className="font-semibold">{h.userName || "Someone"}</span> <span className="text-xs text-muted">in #{h.channel}</span><span className="block truncate text-muted">{h.text}</span></button>
              {h.permalink && <a href={h.permalink} target="_blank" rel="noreferrer" className="text-xs font-semibold text-accent hover:underline">Open in Slack</a>}
            </li>
          ))}
        </ul>
      ))}
    </div>
  );
}

/** Channels and direct messages on the left, the open conversation on the right, with search and threads. */
export function SlackInbox({ workspaces, initialConnectionId }: { workspaces: SlackWorkspace[]; initialConnectionId?: string | null }) {
  const [connectionId, setConnectionId] = useState(initialConnectionId && workspaces.some((w) => w.connectionId === initialConnectionId) ? initialConnectionId : workspaces[0].connectionId);
  const workspace = workspaces.find((w) => w.connectionId === connectionId) ?? workspaces[0];
  const [filter, setFilter] = useState("all");
  const [channels, setChannels] = useState<SlackChannel[] | null>(null);
  const [error, setError] = useState<Error | null>(null);
  const [active, setActive] = useState<SlackChannel | null>(null);
  const [find, setFind] = useState("");
  const [searching, setSearching] = useState(false);

  const load = useCallback(() => {
    setError(null);
    api.slackChannels(connectionId).then(setChannels, (e) => setError(e instanceof Error ? e : new Error("Could not load Slack.")));
  }, [connectionId]);
  useEffect(() => { const t = setTimeout(load, 0); return () => clearTimeout(t); }, [load]);

  if (error) return <ErrorState error={error} onRetry={load} />;
  const shown = (channels ?? []).filter((c) => (filter === "all" || (filter === "dms" ? c.kind === "dm" || c.kind === "group" : c.kind === "channel")) && c.name.toLowerCase().includes(find.trim().toLowerCase()));

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-2">
        {workspaces.length > 1 && <Dropdown className="w-52" value={connectionId} options={workspaces.map((w) => ({ value: w.connectionId, label: w.teamName }))} onChange={(v) => { setChannels(null); setActive(null); setConnectionId(v); }} />}
        <Dropdown className="w-44" value={filter} options={FILTERS} onChange={setFilter} />
        <input value={find} onChange={(e) => setFind(e.target.value)} placeholder="Find a channel or person" aria-label="Find a channel or person" className={`${inputClass} max-w-xs`} />
        <button type="button" onClick={() => setSearching((s) => !s)} aria-pressed={searching} className={secondaryButtonClass}>Search messages</button>
        <button type="button" onClick={load} className={secondaryButtonClass}>Refresh</button>
      </div>
      {searching && <div className={`p-4 ${tileClass}`}><SearchPanel workspace={workspace} onOpen={(id) => { const c = channels?.find((x) => x.id === id); if (c) setActive(c); }} /></div>}
      <div className="flex flex-col gap-3 md:flex-row">
        <aside className={`max-h-[70vh] w-full shrink-0 overflow-auto p-2 md:w-72 ${tileClass}`} aria-label="Conversations">
          {channels === null ? <Loading label="Loading conversations" /> : shown.length === 0 ? <EmptyState>Nothing here.</EmptyState> : (
            <ul>{shown.map((c) => (
              <li key={c.id}><button type="button" onClick={() => setActive(c)} aria-current={active?.id === c.id}
                className={`flex w-full items-center gap-2 rounded-xl px-3 py-2 text-left text-sm ${active?.id === c.id ? "bg-accent text-accent-contrast" : "hover:bg-surface-2"}`}>
                <span aria-hidden>{KIND_LABEL[c.kind]}</span><span className="min-w-0 flex-1 truncate">{c.name}</span>{c.isPrivate && c.kind === "channel" && <span aria-label="Private" className="text-xs">🔒</span>}
              </button></li>
            ))}</ul>
          )}
        </aside>
        {active ? <Conversation key={`${connectionId}-${active.id}`} channel={active} workspace={workspace} /> : <div className={`flex min-h-[20rem] flex-1 items-center justify-center p-6 text-sm text-muted ${tileClass}`}>Choose a channel or person to read messages.</div>}
      </div>
    </div>
  );
}
