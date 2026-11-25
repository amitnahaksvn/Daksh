"use client";
import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { OneAppShell } from "@/components/OneAppParts";
import { ONE_APPS, manageFor } from "@/components/oneAppConfigs";
import { buttonClass, Dropdown, EmptyState, ErrorState, inputClass, Loading, secondaryButtonClass, tileClass } from "@/components/ui";
import { api, ApiError, type DiscordChannel, type DiscordGuild, type DiscordMessage, type OneAppStatus } from "@/lib/api";

const errorText = (err: unknown, fallback: string) => (err instanceof ApiError ? err.message : fallback);
const timeText = (iso: string | null) => (iso ? new Date(iso).toLocaleString([], { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" }) : "");

function Channel({ channel, canWrite }: { channel: DiscordChannel; canWrite: boolean }) {
  const [page, setPage] = useState<{ messages: DiscordMessage[]; older: string | null } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [text, setText] = useState(""); const [busy, setBusy] = useState(false); const [more, setMore] = useState(false);
  const endRef = useRef<HTMLDivElement>(null);
  const load = useCallback(() => {
    api.discordMessages(channel.id).then((p) => setPage({ messages: p.messages, older: p.older }), (e) => { setError(errorText(e, "Could not load messages.")); setPage({ messages: [], older: null }); });
  }, [channel.id]);
  useEffect(() => { const t = setTimeout(load, 0); return () => clearTimeout(t); }, [load]); // keyed by channel, so a new channel starts fresh
  useEffect(() => { if (page && !more) endRef.current?.scrollIntoView({ block: "end" }); }, [page, more]);

  async function older() {
    if (!page?.older) return;
    setMore(true);
    try { const p = await api.discordMessages(channel.id, page.older); setPage({ messages: [...p.messages, ...page.messages], older: p.older }); }
    catch (e) { setError(errorText(e, "Could not load older messages.")); }
    finally { setMore(false); }
  }
  async function send(e: FormEvent) {
    e.preventDefault(); setBusy(true); setError(null);
    try { await api.sendDiscordMessage(channel.id, text.trim()); setText(""); load(); }
    catch (err) { setError(errorText(err, "Could not send the message.")); }
    finally { setBusy(false); }
  }
  return (
    <section className={`flex min-h-[26rem] min-w-0 flex-1 flex-col p-4 ${tileClass}`}>
      <header><h3 className="text-base font-bold"># {channel.name}</h3>{channel.topic && <p className="truncate text-xs text-muted">{channel.topic}</p>}</header>
      {error && <p role="alert" className="mt-2 text-sm text-danger">{error}</p>}
      <div className="mt-3 max-h-[50vh] min-h-0 flex-1 overflow-auto">
        {page === null ? <Loading label="Loading messages" /> : page.messages.length === 0 ? <EmptyState>No messages to show.</EmptyState> : (
          <>
            {page.older && <div className="mb-2 text-center"><button type="button" onClick={() => void older()} disabled={more} className={secondaryButtonClass}>{more ? "Loading…" : "Older messages"}</button></div>}
            <ul className="space-y-3">{page.messages.map((m) => (
              <li key={m.id} className="text-sm"><span className="font-semibold">{m.author}</span> <span className="text-xs text-muted">{timeText(m.time)}</span>{m.isBot && <span className="ml-1 rounded bg-surface-2 px-1 text-[10px] font-bold uppercase text-muted">bot</span>}
                <p className="whitespace-pre-wrap break-words">{m.content || (m.attachments + m.embeds > 0 ? "" : "(no text — turn on Message Content Intent for the bot)")}</p>
                {m.attachments > 0 && <p className="text-xs text-muted">📎 {m.attachments} attachment{m.attachments === 1 ? "" : "s"} (open in Discord)</p>}</li>))}</ul>
            <div ref={endRef} />
          </>
        )}
      </div>
      {canWrite ? (
        <form onSubmit={(e) => void send(e)} className="mt-3 space-y-2">
          <textarea value={text} onChange={(e) => setText(e.target.value)} rows={2} maxLength={2000} aria-label={`Message #${channel.name}`} placeholder={`Message #${channel.name}`} className={`${inputClass} resize-y`} />
          <button type="submit" disabled={busy || !text.trim()} className={buttonClass}>{busy ? "Sending…" : "Send as the bot"}</button>
        </form>
      ) : <p className="mt-3 text-xs text-muted">Daksha has read-only access to Discord. Turn on full access in Manage to post.</p>}
    </section>
  );
}

function Servers({ status }: { status: OneAppStatus }) {
  const [guilds, setGuilds] = useState<DiscordGuild[] | null>(null); const [error, setError] = useState<Error | null>(null);
  const [guild, setGuild] = useState(""); const [channels, setChannels] = useState<DiscordChannel[] | null>(null); const [active, setActive] = useState<DiscordChannel | null>(null);
  useEffect(() => { const t = setTimeout(() => { api.discordGuilds().then((g) => { setGuilds(g); setGuild((c) => c || g[0]?.id || ""); }, (e) => setError(e instanceof Error ? e : new Error("Could not load Discord."))); }, 0); return () => clearTimeout(t); }, []);
  useEffect(() => {
    if (!guild) return;
    let live = true;
    const t = setTimeout(() => { api.discordChannels(guild).then((c) => live && setChannels(c), () => live && setChannels([])); }, 0);
    return () => { live = false; clearTimeout(t); };
  }, [guild]);
  if (error) return <ErrorState error={error} />;
  if (guilds === null) return <Loading label="Loading servers" />;
  if (guilds.length === 0) return <EmptyState>The bot is not in any server yet. Open Manage and use “Invite the bot to a server”.</EmptyState>;
  return (
    <div className="space-y-3">
      <Dropdown className="w-64" value={guild} options={guilds.map((g) => ({ value: g.id, label: g.name }))} onChange={(v) => { setChannels(null); setActive(null); setGuild(v); }} />
      <div className="flex flex-col gap-3 md:flex-row">
        <aside className={`max-h-[70vh] w-full shrink-0 overflow-auto p-2 md:w-64 ${tileClass}`} aria-label="Channels">
          {channels === null ? <Loading label="Loading channels" /> : channels.length === 0 ? <EmptyState>No text channels the bot can see.</EmptyState> : (
            <ul>{channels.map((c) => <li key={c.id}><button type="button" onClick={() => setActive(c)} aria-current={active?.id === c.id} className={`flex w-full items-center gap-2 rounded-xl px-3 py-2 text-left text-sm ${active?.id === c.id ? "bg-accent text-accent-contrast" : "hover:bg-surface-2"}`}><span aria-hidden>{c.kind === "announcement" ? "📢" : "#"}</span><span className="min-w-0 flex-1 truncate">{c.name}</span></button></li>)}</ul>
          )}
        </aside>
        {active ? <Channel key={active.id} channel={active} canWrite={status.write} /> : <div className={`flex min-h-[20rem] flex-1 items-center justify-center p-6 text-sm text-muted ${tileClass}`}>Choose a channel to read its messages.</div>}
      </div>
    </div>
  );
}

/** The /apps/discord page: the servers the bot is in, their text channels, messages, and (with full access) posting as the bot. */
export function DiscordWorkspace() {
  return <OneAppShell config={ONE_APPS.discord} manage={manageFor("discord")}>{(status) => <Servers status={status} />}</OneAppShell>;
}
