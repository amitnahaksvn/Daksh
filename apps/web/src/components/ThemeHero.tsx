"use client";
import { useEffect, useState } from "react";
import { api, type ConnectionStatusDto, type DashboardApp } from "@/lib/api";
import { Orb } from "@/components/Orb";
import { tileClass } from "@/components/ui";
import { useStyle } from "@/components/ThemeToggle";

/** What the hero says. `null` while the Command Centre is still loading. */
export type HeroStats = { needs: number; handled: number; next: string | null; first: string | null };

const greeting = () => { const h = new Date().getHours(); return h < 5 ? "Working late" : h < 12 ? "Good morning" : h < 17 ? "Good afternoon" : "Good evening"; };
const dateLine = () => new Date().toLocaleDateString(undefined, { weekday: "long", day: "numeric", month: "long" });
const sentence = (s: HeroStats | null) => !s ? "Gathering your day…" : s.needs === 0 ? `Nothing needs you. ${s.handled} handled today.` : `${s.needs} thing${s.needs === 1 ? "" : "s"} need${s.needs === 1 ? "s" : ""} you. ${s.handled} handled today.`;
const healthy = (apps: DashboardApp[]) => apps.filter((a) => a.status === "Connected").length;
const nodeColour = (a: DashboardApp) => a.status === "Connected" ? "var(--accent)" : "var(--danger)";

/** Sandhya: an editorial morning briefing. */
function Editorial({ stats }: { stats: HeroStats | null }) {
  return (
    <section className={`p-6 sm:p-8 ${tileClass}`}>
      <p className="text-xs font-semibold uppercase tracking-[0.14em] text-accent">{dateLine()}</p>
      <h1 className="mt-2 text-4xl font-medium leading-tight sm:text-5xl">{greeting()}.</h1>
      <p className="mt-2 max-w-xl text-lg text-muted">{sentence(stats)}</p>
    </section>
  );
}

/** Sandhya x Yantra: warm greeting with instrument-style counters. */
function Hybrid({ stats, apps }: { stats: HeroStats | null; apps: DashboardApp[] }) {
  return (
    <section className={`grid gap-4 p-6 sm:grid-cols-[1fr_auto] sm:items-end ${tileClass}`}>
      <div>
        <p className="ai-mono text-xs font-medium uppercase tracking-widest text-accent">{dateLine()} · {healthy(apps)} of {apps.length} apps healthy</p>
        <h1 className="mt-1 text-4xl font-medium leading-tight sm:text-5xl">{greeting()}.</h1>
        <p className="mt-1 text-muted">{sentence(stats)}</p>
      </div>
      <div className="flex gap-3">
        <div className="rounded-2xl bg-text px-4 py-2 text-canvas"><p className="ai-mono text-[10px] uppercase tracking-wider opacity-70">Needs you</p><p className="text-3xl leading-none">{stats?.needs ?? "–"}</p></div>
        <div className="rounded-2xl border-2 border-text px-4 py-2"><p className="ai-mono text-[10px] uppercase tracking-wider text-muted">Handled</p><p className="text-3xl leading-none">{stats?.handled ?? "–"}</p></div>
      </div>
    </section>
  );
}

/** Prana: the orb and one question. */
function Conversation({ stats }: { stats: HeroStats | null }) {
  return (
    <section className={`flex flex-col items-center gap-3 px-6 py-8 text-center ${tileClass}`}>
      <Orb size={96} />
      <h1 className="text-3xl font-medium sm:text-4xl">What should I take care of?</h1>
      <p className="text-muted">{sentence(stats)}</p>
    </section>
  );
}

/** Yantra: a panel with big counters and a status strip. */
function Panel({ stats, apps }: { stats: HeroStats | null; apps: DashboardApp[] }) {
  const ok = healthy(apps) === apps.length;
  return (
    <section className="grid gap-3 sm:grid-cols-12">
      <div className="rounded-[0.9rem] border-2 border-text bg-text p-5 text-canvas sm:col-span-8">
        <p className="ai-mono text-xs tracking-widest text-accent">01 · COMMAND</p>
        <h1 className="mt-1 text-3xl font-semibold sm:text-4xl">{greeting()}. Tell Daksha what to do.</h1>
        <p className="ai-mono mt-2 text-xs opacity-70">{dateLine()} · {ok ? "ALL SYSTEMS NORMAL" : "ATTENTION NEEDED"}</p>
      </div>
      <div className="grid grid-cols-2 gap-3 sm:col-span-4 sm:grid-cols-1">
        <div className={`p-4 ${tileClass}`}><p className="ai-mono text-[11px] text-muted">NEEDS YOU</p><p className="text-4xl font-bold leading-none text-accent">{stats?.needs ?? "–"}</p></div>
        <div className={`p-4 ${tileClass}`}><p className="ai-mono text-[11px] text-muted">HANDLED TODAY</p><p className="text-4xl font-bold leading-none">{stats?.handled ?? "–"}</p></div>
      </div>
    </section>
  );
}

/** Rangoli: one huge colour block. */
function Block({ stats }: { stats: HeroStats | null }) {
  return (
    <section className="rounded-[1.75rem] bg-accent p-7 text-accent-contrast sm:p-9">
      <h1 className="text-5xl font-extrabold leading-[0.98] sm:text-7xl">Hey,<br />{stats ? (stats.needs === 0 ? "all clear." : `${stats.needs} thing${stats.needs === 1 ? "" : "s"} need you.`) : "one sec…"}</h1>
      <p className="mt-3 text-lg font-semibold opacity-90">{stats ? `${stats.handled} done for you today.` : ""}</p>
    </section>
  );
}

/** Ratna: serif, numbered, hairlines. */
function Ledger({ stats }: { stats: HeroStats | null }) {
  return (
    <section className={`p-8 ${tileClass}`}>
      <p className="text-xs uppercase tracking-[0.22em] text-accent">{dateLine()}</p>
      <h1 className="mt-3 text-5xl font-medium leading-none sm:text-6xl">{greeting()}</h1>
      <p className="mt-3 text-lg text-muted">{stats ? (stats.needs === 0 ? "No matter awaits your word." : `${stats.needs} matter${stats.needs === 1 ? "" : "s"} await your word. ${stats.handled} already settled.`) : "Gathering…"}</p>
    </section>
  );
}

/** Galaxy: the lotus core with every app in orbit. */
function Orbit({ stats, apps }: { stats: HeroStats | null; apps: DashboardApp[] }) {
  const shown = apps.slice(0, 6);
  return (
    <section className={`relative overflow-hidden p-6 text-center ${tileClass}`}>
      <svg viewBox="0 0 560 300" role="img" aria-label="Your apps orbiting Daksha" className="mx-auto h-56 w-full max-w-xl">
        <g fill="none" stroke="var(--line)" strokeWidth="1.4"><circle cx="280" cy="150" r="62" /><circle cx="280" cy="150" r="112" strokeDasharray="3 7" /><ellipse cx="280" cy="150" rx="200" ry="104" transform="rotate(-14 280 150)" /></g>
        <circle cx="280" cy="150" r="34" fill="var(--accent)" opacity=".25" /><circle cx="280" cy="150" r="22" fill="var(--accent)" />
        {shown.map((a, i) => { const ang = (i / Math.max(shown.length, 1)) * Math.PI * 2 - 1; const r = i % 2 ? 112 : 62; const x = 280 + Math.cos(ang) * r * (i % 2 ? 1.5 : 1.1); const y = 150 + Math.sin(ang) * r * (i % 2 ? .75 : 1);
          return <g key={a.id}><circle cx={x} cy={y} r={a.unreadCount > 0 ? 11 : 8} fill={nodeColour(a)} /><text x={x} y={y + 24} textAnchor="middle" fontSize="12" fontWeight="600" fill="var(--text)">{a.name}</text></g>; })}
      </svg>
      <h1 className="text-2xl font-medium sm:text-3xl">{stats && stats.needs === 0 ? "Your universe is calm." : "A star is glowing."}</h1>
      <p className="text-muted">{sentence(stats)}</p>
    </section>
  );
}

/** Nakshatra: a star map. Bigger and brighter means more unread; red means a broken connection. */
function StarMap({ stats, apps }: { stats: HeroStats | null; apps: DashboardApp[] }) {
  const shown = apps.slice(0, 7);
  return (
    <section className={`p-6 ${tileClass}`}>
      <div className="grid items-center gap-4 sm:grid-cols-[1fr_1.2fr]">
        <div>
          <p className="ai-mono text-xs uppercase tracking-widest text-accent">Your sky tonight</p>
          <h1 className="mt-1 text-3xl font-extrabold sm:text-4xl">{greeting()}.</h1>
          <p className="mt-1 text-muted">{sentence(stats)}</p>
          <p className="ai-mono mt-3 text-xs text-muted">bigger = more unread · red = broken</p>
        </div>
        <svg viewBox="0 0 420 220" role="img" aria-label="Star map of your apps" className="w-full">
          <g stroke="var(--line)" strokeWidth="1.2">{shown.map((a, i) => { const ang = (i / Math.max(shown.length, 1)) * Math.PI * 2; return <line key={a.id} x1="210" y1="110" x2={210 + Math.cos(ang) * 150} y2={110 + Math.sin(ang) * 84} />; })}</g>
          <circle cx="210" cy="110" r="14" fill="var(--accent)" /><text x="210" y="114" textAnchor="middle" fontSize="10" fontWeight="700" fill="var(--accent-contrast)">You</text>
          {shown.map((a, i) => { const ang = (i / Math.max(shown.length, 1)) * Math.PI * 2; const x = 210 + Math.cos(ang) * 150; const y = 110 + Math.sin(ang) * 84;
            return <g key={a.id}><circle cx={x} cy={y} r={6 + Math.min(a.unreadCount, 8)} fill={nodeColour(a)} opacity={a.unreadCount ? 1 : .7} /><text x={x} y={y + 22} textAnchor="middle" fontSize="11" fontWeight="600" fill="var(--text)">{a.name}</text></g>; })}
        </svg>
      </div>
    </section>
  );
}

/** Mitra: a friendly lotus with a speech bubble. */
function Mascot({ stats }: { stats: HeroStats | null }) {
  return (
    <section className={`flex flex-wrap items-center gap-5 p-6 ${tileClass}`}>
      <svg width="110" height="110" viewBox="0 0 190 190" role="img" aria-label="Daksha the lotus assistant, smiling">
        <ellipse cx="95" cy="80" rx="26" ry="52" fill="var(--orb-b)" /><ellipse cx="60" cy="96" rx="26" ry="48" transform="rotate(-40 60 96)" fill="var(--orb-a)" /><ellipse cx="130" cy="96" rx="26" ry="48" transform="rotate(40 130 96)" fill="var(--orb-a)" />
        <circle cx="95" cy="112" r="50" fill="var(--lime)" /><circle cx="78" cy="106" r="6" fill="var(--text)" /><circle cx="112" cy="106" r="6" fill="var(--text)" /><path d="M78 124c8 10 26 10 34 0" stroke="var(--text)" strokeWidth="4" fill="none" strokeLinecap="round" />
      </svg>
      <div className="min-w-0 flex-1 rounded-[2rem] rounded-bl-md bg-surface-2 px-5 py-4">
        <h1 className="text-2xl font-bold sm:text-3xl">{greeting()}! {stats ? (stats.needs === 0 ? "All clear." : `I have ${stats.needs} thing${stats.needs === 1 ? "" : "s"} to ask you.`) : "One moment…"}</h1>
        <p className="mt-1 text-muted">{stats?.first ?? sentence(stats)}</p>
      </div>
    </section>
  );
}

/** Kavach: you are in control. Real switches that turn auto-reply on or off for each connected messaging app. */
function Dials({ apps }: { apps: DashboardApp[] }) {
  const messaging = apps.filter((a) => (a.integrationKey === "whatsapp" || a.integrationKey === "telegram") && a.status === "Connected");
  const [status, setStatus] = useState<Record<string, ConnectionStatusDto>>({});
  const [error, setError] = useState("");
  const ids = messaging.map((a) => a.connectionId).join(",");
  useEffect(() => {
    let live = true;
    ids.split(",").filter(Boolean).forEach((id) => api.getConnectionStatus(id).then((s) => { if (live) setStatus((m) => ({ ...m, [id]: s })); }, () => undefined));
    return () => { live = false; };
  }, [ids]);
  async function set(id: string, autoReplyEnabled: boolean) {
    setError("");
    try { const s = await api.updateConnectionSettings(id, { autoReplyEnabled }); setStatus((m) => ({ ...m, [id]: s })); }
    catch (e) { setError(e instanceof Error ? e.message : "Could not change that setting."); }
  }
  return (
    <section className={`p-6 ${tileClass}`}>
      <div className="flex items-center gap-3">
        <svg width="40" height="40" viewBox="0 0 46 46" aria-hidden="true"><path d="M23 4l16 6v11c0 10-7 17-16 21C14 38 7 31 7 21V10z" fill="var(--accent)" /><path d="M16 23l5 5 9-10" fill="none" stroke="var(--accent-contrast)" strokeWidth="3.5" strokeLinecap="round" strokeLinejoin="round" /></svg>
        <div><h1 className="text-2xl font-bold sm:text-3xl">You are in control</h1><p className="text-sm text-muted">Decide whether Daksha may reply on its own. Change it any time.</p></div>
      </div>
      {error && <p role="alert" className="mt-3 rounded-2xl bg-danger-bg px-4 py-2 text-sm text-danger">{error}</p>}
      {messaging.length === 0
        ? <p className="mt-4 text-sm text-muted">Connect WhatsApp or Telegram to set how much Daksha may do.</p>
        : <div className="mt-4 grid gap-3 sm:grid-cols-2">{messaging.map((a) => { const on = status[a.connectionId]?.autoReplyEnabled; return (
            <div key={a.id} className="rounded-2xl border border-line bg-surface-2 p-4">
              <div className="flex items-center justify-between"><span className="font-semibold">{a.name}</span><span className="text-xs font-semibold text-accent">● Connected</span></div>
              <div role="radiogroup" aria-label={`${a.name} auto-reply`} className="mt-3 flex rounded-xl bg-canvas p-1 text-sm font-semibold">
                <button type="button" role="radio" aria-checked={on === false} disabled={on === undefined} onClick={() => void set(a.connectionId, false)} className={`flex-1 rounded-lg py-2 ${on === false ? "bg-accent text-accent-contrast" : "text-muted"}`}>Ask me</button>
                <button type="button" role="radio" aria-checked={on === true} disabled={on === undefined} onClick={() => void set(a.connectionId, true)} className={`flex-1 rounded-lg py-2 ${on === true ? "bg-accent text-accent-contrast" : "text-muted"}`}>Auto-reply</button>
              </div>
              <p className="mt-2 text-xs text-muted">{on === true ? "Daksha may reply on its own." : on === false ? "Daksha only drafts. You decide." : "Checking…"}</p>
            </div>); })}</div>}
    </section>
  );
}

/** The dashboard's opening block. Every theme has its own: this is where each one's character shows. */
export function ThemeHero({ apps, stats }: { apps: DashboardApp[]; stats: HeroStats | null }) {
  const style = useStyle();
  switch (style) {
    case "sandhya": return <Editorial stats={stats} />;
    case "prana": return <Conversation stats={stats} />;
    case "yantra": return <Panel stats={stats} apps={apps} />;
    case "rangoli": return <Block stats={stats} />;
    case "ratna": return <Ledger stats={stats} />;
    case "galaxy": return <Orbit stats={stats} apps={apps} />;
    case "nakshatra": return <StarMap stats={stats} apps={apps} />;
    case "mitra": return <Mascot stats={stats} />;
    case "kavach": return <Dials apps={apps} />;
    default: return <Hybrid stats={stats} apps={apps} />;
  }
}
