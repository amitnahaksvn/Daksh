"use client";
/* eslint-disable @next/next/no-img-element -- album art comes from Spotify's CDN */
import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { OneAppShell, Rows } from "@/components/OneAppParts";
import { ONE_APPS, manageFor } from "@/components/oneAppConfigs";
import { Dropdown, EmptyState, ErrorState, inputClass, Loading, secondaryButtonClass, tileClass } from "@/components/ui";
import { api, ApiError, type OneAppStatus, type SpotifyNow, type SpotifyPlaylist, type SpotifyTrack } from "@/lib/api";

const errorText = (err: unknown, fallback: string) => (err instanceof ApiError ? err.message : fallback);
const mmss = (ms: number) => `${Math.floor(ms / 60000)}:${String(Math.floor((ms % 60000) / 1000)).padStart(2, "0")}`;
const TABS = [{ id: "player", label: "Player" }, { id: "recent", label: "Recently played" }, { id: "top", label: "Top tracks" }, { id: "playlists", label: "Playlists" }, { id: "search", label: "Search" }];
const RANGES = [{ value: "short_term", label: "Last 4 weeks" }, { value: "medium_term", label: "Last 6 months" }, { value: "long_term", label: "All time" }];

function TrackRows({ tracks, canWrite, onMessage }: { tracks: SpotifyTrack[]; canWrite: boolean; onMessage: (m: string) => void }) {
  async function act(fn: () => Promise<void>, ok: string) { try { await fn(); onMessage(ok); } catch (e) { onMessage(errorText(e, "Could not do that.")); } }
  if (tracks.length === 0) return <EmptyState>Nothing here.</EmptyState>;
  return (
    <Rows>{tracks.map((t, i) => (
      <li key={`${t.id}-${i}`} className="flex items-center gap-3 px-3 py-2">
        {t.image ? <img src={t.image} alt="" className="h-9 w-9 rounded" /> : <span className="h-9 w-9 rounded bg-surface-2" aria-hidden />}
        <span className="min-w-0 flex-1"><span className="block truncate text-sm font-semibold">{t.name}</span><span className="block truncate text-xs text-muted">{t.artists} · {t.album}</span></span>
        <span className="hidden shrink-0 text-xs text-muted sm:block">{mmss(t.durationMs)}</span>
        {canWrite && t.uri && <span className="flex shrink-0 gap-1"><button type="button" className="rounded-full border border-line px-3 py-1 text-xs font-semibold hover:bg-surface-2" onClick={() => void act(() => api.spotifyPlay({ uri: t.uri }), "Playing")}>Play</button>
          <button type="button" className="rounded-full border border-line px-3 py-1 text-xs font-semibold hover:bg-surface-2" onClick={() => void act(() => api.spotifyQueue(t.uri), "Added to queue")}>Queue</button></span>}
      </li>))}</Rows>
  );
}

function Player({ status, onMessage }: { status: OneAppStatus; onMessage: (m: string) => void }) {
  const [now, setNow] = useState<SpotifyNow | null>(null); const [error, setError] = useState<Error | null>(null);
  const alive = useRef(true);
  const refresh = () => api.spotifyNow().then((n) => alive.current && (setNow(n), setError(null)), (e) => alive.current && setError(e instanceof Error ? e : new Error("Could not load Spotify.")));
  useEffect(() => { alive.current = true; const t = setTimeout(() => void refresh(), 0); const i = window.setInterval(() => void refresh(), 10000); return () => { alive.current = false; clearTimeout(t); window.clearInterval(i); }; }, []);
  async function control(action: string, extra: { value?: number; mode?: string } = {}) {
    try { await api.spotifyControl(action, extra); await refresh(); } catch (e) { onMessage(errorText(e, "Could not control playback.")); }
  }
  if (error) return <ErrorState error={error} onRetry={() => void refresh()} />;
  if (!now) return <Loading label="Loading player" />;
  const premium = status.extra.product === "premium";
  return (
    <section className={`p-4 ${tileClass}`}>
      {!now.active || !now.track ? <EmptyState>Nothing is playing. Start Spotify on a phone or computer and it will show up here.</EmptyState> : (
        <div className="flex flex-wrap items-center gap-4">
          {now.track.image ? <img src={now.track.image} alt="" className="h-24 w-24 rounded-xl" /> : null}
          <div className="min-w-0 flex-1">
            <p className="text-xs font-bold uppercase text-muted">{now.isPlaying ? "Now playing" : "Paused"}{now.device ? ` · ${now.device}` : ""}</p>
            <h3 className="truncate text-xl font-bold">{now.track.name}</h3>
            <p className="truncate text-sm text-muted">{now.track.artists} · {now.track.album}</p>
            <div className="mt-2 h-1.5 overflow-hidden rounded-full bg-surface-2" role="progressbar" aria-valuenow={now.progressMs} aria-valuemax={now.track.durationMs}><div className="h-full bg-accent" style={{ width: `${Math.min(100, (now.progressMs / Math.max(1, now.track.durationMs)) * 100)}%` }} /></div>
            <p className="mt-1 text-xs text-muted">{mmss(now.progressMs)} / {mmss(now.track.durationMs)}</p>
          </div>
        </div>
      )}
      {status.write ? (
        <div className="mt-4 flex flex-wrap items-center gap-2">
          <button type="button" className={secondaryButtonClass} onClick={() => void control("previous")} aria-label="Previous">⏮</button>
          <button type="button" className={secondaryButtonClass} onClick={() => void control(now.isPlaying ? "pause" : "play")}>{now.isPlaying ? "Pause" : "Play"}</button>
          <button type="button" className={secondaryButtonClass} onClick={() => void control("next")} aria-label="Next">⏭</button>
          <button type="button" aria-pressed={now.shuffle} className={secondaryButtonClass} onClick={() => void control("shuffle", { mode: String(!now.shuffle) })}>Shuffle {now.shuffle ? "on" : "off"}</button>
          <button type="button" className={secondaryButtonClass} onClick={() => void control("repeat", { mode: now.repeat === "off" ? "context" : now.repeat === "context" ? "track" : "off" })}>Repeat {now.repeat}</button>
          <label className="flex items-center gap-2 text-xs text-muted">Volume<input type="range" min={0} max={100} defaultValue={now.volume ?? 50} onMouseUp={(e) => void control("volume", { value: Number(e.currentTarget.value) })} onKeyUp={(e) => void control("volume", { value: Number(e.currentTarget.value) })} aria-label="Volume" /></label>
          {!premium && <p className="w-full text-xs text-muted">Spotify only allows playback control with a Premium account.</p>}
        </div>
      ) : <p className="mt-3 text-xs text-muted">Daksha has read-only access to Spotify. Turn on full access in Manage to control playback.</p>}
    </section>
  );
}

function Loader<T>({ load, keyed, children }: { load: () => Promise<T>; keyed: string; children: (d: T) => React.ReactNode }) {
  const [state, setState] = useState<{ data: T | null; error: Error | null; key: string }>({ data: null, error: null, key: keyed });
  const [tick, setTick] = useState(0);
  const loadRef = useRef(load);
  useEffect(() => { loadRef.current = load; });
  useEffect(() => {
    let live = true;
    const t = setTimeout(() => { loadRef.current().then((d) => live && setState({ data: d, error: null, key: keyed }), (e) => live && setState({ data: null, error: e instanceof Error ? e : new Error("Could not load."), key: keyed })); }, 0);
    return () => { live = false; clearTimeout(t); };
  }, [keyed, tick]);
  if (state.key === keyed && state.error) return <ErrorState error={state.error} onRetry={() => setTick((n) => n + 1)} />;
  if (state.key !== keyed || state.data === null) return <Loading label="Loading" />;
  return <>{children(state.data)}</>;
}

function Playlists({ canWrite, onMessage }: { canWrite: boolean; onMessage: (m: string) => void }) {
  const [open, setOpen] = useState<SpotifyPlaylist | null>(null);
  if (open) return (
    <section className={`p-4 ${tileClass}`}>
      <div className="flex items-center gap-2"><button type="button" onClick={() => setOpen(null)} className={secondaryButtonClass}>Back</button><h3 className="min-w-0 flex-1 truncate text-base font-bold">{open.name}</h3>
        {canWrite && <button type="button" className={secondaryButtonClass} onClick={() => void api.spotifyPlay({ contextUri: open.uri }).then(() => onMessage("Playing"), (e) => onMessage(errorText(e, "Could not play it.")))}>Play playlist</button>}</div>
      <div className="mt-3"><Loader load={() => api.spotifyPlaylistTracks(open.id)} keyed={open.id}>{(tracks) => <TrackRows tracks={tracks} canWrite={canWrite} onMessage={onMessage} />}</Loader></div>
    </section>
  );
  return (
    <section className={`p-4 ${tileClass}`}>
      <Loader load={() => api.spotifyPlaylists()} keyed="playlists">{(lists) => lists.length === 0 ? <EmptyState>No playlists.</EmptyState> : (
        <Rows>{lists.map((p) => <li key={p.id}><button type="button" onClick={() => setOpen(p)} className="flex w-full items-center gap-3 px-3 py-2 text-left">
          {p.image ? <img src={p.image} alt="" className="h-9 w-9 rounded" /> : <span className="h-9 w-9 rounded bg-surface-2" aria-hidden />}
          <span className="min-w-0 flex-1"><span className="block truncate text-sm font-semibold hover:underline">{p.name}</span><span className="block truncate text-xs text-muted">{p.tracks} tracks{p.owner ? ` · ${p.owner}` : ""}</span></span></button></li>)}</Rows>
      )}</Loader>
    </section>
  );
}

function Search({ canWrite, onMessage }: { canWrite: boolean; onMessage: (m: string) => void }) {
  const [q, setQ] = useState(""); const [applied, setApplied] = useState("");
  return (
    <section className={`p-4 ${tileClass}`}>
      <form onSubmit={(e: FormEvent) => { e.preventDefault(); setApplied(q.trim()); }}><input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search songs and playlists" aria-label="Search Spotify" maxLength={200} className={inputClass} /></form>
      {applied && <div className="mt-3"><Loader load={() => api.spotifySearch(applied)} keyed={`s|${applied}`}>{(r) => (
        <div className="space-y-4"><TrackRows tracks={r.tracks} canWrite={canWrite} onMessage={onMessage} />
          {r.playlists.length > 0 && <div><h4 className="mb-1 text-sm font-bold">Playlists</h4><Rows>{r.playlists.map((p) => <li key={p.id} className="flex items-center gap-3 px-3 py-2"><a href={p.url ?? "#"} target="_blank" rel="noreferrer" className="min-w-0 flex-1 truncate text-sm hover:underline">{p.name}</a>
            {canWrite && <button type="button" className="rounded-full border border-line px-3 py-1 text-xs font-semibold hover:bg-surface-2" onClick={() => void api.spotifyPlay({ contextUri: p.uri }).then(() => onMessage("Playing"), (e) => onMessage(errorText(e, "Could not play it.")))}>Play</button>}</li>)}</Rows></div>}</div>
      )}</Loader></div>}
    </section>
  );
}

/** The /apps/spotify page: what is playing (with controls on full access), recent plays, top tracks, playlists and search. */
export function SpotifyWorkspace() {
  const [tab, setTab] = useState(() => (typeof window === "undefined" ? null : new URLSearchParams(window.location.search).get("tab")) ?? "player");
  const [range, setRange] = useState("medium_term");
  const [message, setMessage] = useState<string | null>(null);
  return (
    <OneAppShell config={ONE_APPS.spotify} manage={manageFor("spotify")}>
      {(status) => (
        <>
          <div role="tablist" aria-label="Spotify" className="flex flex-wrap gap-1">
            {TABS.map((t) => <button key={t.id} role="tab" aria-selected={tab === t.id} onClick={() => setTab(t.id)} className={`rounded-full px-4 py-2 text-sm font-semibold ${tab === t.id ? "bg-accent text-accent-contrast" : "border border-line text-muted hover:bg-surface-2"}`}>{t.label}</button>)}
          </div>
          {message && <p role="status" className="rounded-[1.25rem] border border-line bg-surface p-3 text-sm">{message}</p>}
          {tab === "player" && <Player status={status} onMessage={setMessage} />}
          {tab === "recent" && <section className={`p-4 ${tileClass}`}><Loader load={() => api.spotifyRecent()} keyed="recent">{(t) => <TrackRows tracks={t} canWrite={status.write} onMessage={setMessage} />}</Loader></section>}
          {tab === "top" && <section className={`p-4 ${tileClass}`}><Dropdown className="w-48" value={range} options={RANGES} onChange={setRange} /><div className="mt-3"><Loader load={() => api.spotifyTop(range)} keyed={`top|${range}`}>{(t) => <TrackRows tracks={t} canWrite={status.write} onMessage={setMessage} />}</Loader></div></section>}
          {tab === "playlists" && <Playlists canWrite={status.write} onMessage={setMessage} />}
          {tab === "search" && <Search canWrite={status.write} onMessage={setMessage} />}
        </>
      )}
    </OneAppShell>
  );
}
