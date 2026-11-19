"use client";
import Link from "next/link";
import { useState, useSyncExternalStore } from "react";
import { api, type AuditEvent, type DashboardApp, type ReminderDto, type TaskDto } from "@/lib/api";
import { useApi } from "@/lib/useApi";
import { buildNeedsYou, buildPulse, buildToday, isView, nextUp, relativeTime, VIEW_KEY, type DashboardView, type NeedsItem, type PendingWithConnection } from "@/lib/commandCentre";
import { Chip, tileClass } from "@/components/ui";
import { ThemeHero } from "@/components/ThemeHero";

type Data = { tasks: TaskDto[]; reminders: ReminderDto[]; audit: AuditEvent[]; pending: PendingWithConnection[] };

/** Loads everything the Command Centre needs. A source that fails just contributes nothing, so one broken app never blanks the dashboard. */
async function loadAll(apps: DashboardApp[]): Promise<Data> {
  const whatsapp = apps.filter((a) => a.integrationKey === "whatsapp" && a.status === "Connected");
  const [tasks, reminders, audit, pendingLists] = await Promise.all([
    api.tasks().catch(() => [] as TaskDto[]),
    api.reminders().catch(() => [] as ReminderDto[]),
    api.auditEvents().catch(() => [] as AuditEvent[]),
    Promise.all(whatsapp.map((a) => api.whatsAppPendingReplies(a.connectionId).then((list) => list.map((reply) => ({ connectionId: a.connectionId, reply })), () => [] as PendingWithConnection[]))),
  ]);
  return { tasks, reminders, audit, pending: pendingLists.flat() };
}

/** Marks a placeholder that is not wired to anything yet, so nobody mistakes sample content for real data. */
export function FakeBadge({ className = "" }: { className?: string }) {
  return <span title="Placeholder: this is not connected to real data yet" className={`ai-mono inline-flex items-center rounded-full border border-dashed border-current px-2 py-0.5 text-[10px] font-bold uppercase tracking-wider text-danger ${className}`}>Fake</span>;
}

const SOURCE_STYLE: Record<NeedsItem["source"], { label: string; tone: string }> = {
  WA: { label: "WA", tone: "bg-lime text-on-tint" },
  TASK: { label: "TASK", tone: "bg-pink text-on-tint" },
  APP: { label: "APP", tone: "bg-danger-bg text-danger" },
  UNREAD: { label: "NEW", tone: "bg-surface-2 text-text" },
};
const btn = "rounded-full bg-accent px-4 py-2 text-xs font-semibold text-accent-contrast hover:opacity-90 disabled:opacity-50";
const btn2 = "rounded-full border border-line px-3.5 py-2 text-xs font-semibold text-text hover:bg-surface-2 disabled:opacity-50";

function AskBar() {
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const [answer, setAnswer] = useState<{ reply: string; simulated: boolean } | null>(null);
  const [error, setError] = useState("");
  async function submit(event: React.FormEvent) {
    event.preventDefault();
    const message = text.trim();
    if (!message || busy) return;
    setBusy(true); setError("");
    try { const r = await api.sendMessage(message); setAnswer({ reply: r.reply, simulated: r.isSimulated }); setText(""); }
    catch (e) { setError(e instanceof Error ? e.message : "Daksha could not answer."); }
    finally { setBusy(false); }
  }
  return (
    <div className="space-y-2">
      <form onSubmit={submit} className={`flex items-center gap-2 p-2 pl-4 ${tileClass}`}>
        <label className="sr-only" htmlFor="cc-ask">Ask Daksha</label>
        <input id="cc-ask" value={text} onChange={(e) => setText(e.target.value)} placeholder="Ask Daksha — “remind me to call Rohan at 8”" className="min-w-0 flex-1 bg-transparent py-2 text-sm text-text placeholder:text-muted focus:outline-none" />
        <button type="submit" disabled={busy || !text.trim()} className={btn}>{busy ? "Thinking…" : "Ask"}</button>
      </form>
      {error && <p role="alert" className="rounded-2xl bg-danger-bg px-4 py-2 text-sm text-danger">{error}</p>}
      {answer && (
        <div className={`p-4 text-sm ${tileClass}`} role="status">
          <div className="mb-1 flex items-center gap-2"><span className="font-semibold">Daksha</span>{answer.simulated && <Chip>simulated</Chip>}</div>
          <p className="whitespace-pre-wrap">{answer.reply}</p>
        </div>
      )}
    </div>
  );
}

/** A search-everything box that is not wired up yet. It looks real but does nothing, and says so. */
function FakeSearch() {
  return (
    <div className="flex items-center gap-2 rounded-2xl border border-dashed border-danger/60 bg-surface-2 px-4 py-2.5 text-sm text-muted" aria-label="Search everything (placeholder)">
      <span className="min-w-0 flex-1 truncate">Search every app — messages, mail, tasks, people…</span>
      <span className="ai-mono rounded-md border border-line px-1.5 py-0.5 text-xs">⌘K</span>
      <FakeBadge />
    </div>
  );
}

function NeedsRow({ item, busy, onAct }: { item: NeedsItem; busy: boolean; onAct: (a: NonNullable<NeedsItem["action"] | NeedsItem["secondary"]>) => void }) {
  const style = SOURCE_STYLE[item.source];
  return (
    <li className="flex flex-wrap items-center gap-3 py-3">
      <span className={`ai-mono w-12 rounded-md py-1 text-center text-[11px] font-bold ${style.tone}`}>{style.label}</span>
      <div className="min-w-0 flex-1"><p className="truncate text-sm font-semibold">{item.title}</p><p className="truncate text-xs text-muted">{item.detail}</p></div>
      {item.action.kind === "link"
        ? <Link href={item.action.href} className={btn}>{item.action.label}</Link>
        : <button type="button" disabled={busy} onClick={() => onAct(item.action)} className={btn}>{item.action.label}</button>}
      {item.secondary && <button type="button" disabled={busy} onClick={() => onAct(item.secondary!)} className={btn2}>{item.secondary.label}</button>}
    </li>
  );
}

function Pulse({ data, apps }: { data: Data; apps: DashboardApp[] }) {
  const p = buildPulse({ audit: data.audit, tasks: data.tasks, apps });
  const stat = (label: string, value: string | number, hint: string, warn = false) => (
    <div className={`p-4 ${tileClass}`}><p className="ai-mono text-[11px] font-semibold uppercase tracking-wider text-muted">{label}</p><p className="mt-1 text-3xl font-extrabold leading-none">{value}</p><p className={`mt-1.5 text-xs ${warn ? "font-semibold text-danger" : "text-muted"}`}>{hint}</p></div>
  );
  const fake = (label: string, value: string, hint: string) => (
    <div className={`border-dashed p-4 ${tileClass}`}><div className="flex items-center justify-between"><p className="ai-mono text-[11px] font-semibold uppercase tracking-wider text-muted">{label}</p><FakeBadge /></div><p className="mt-1 text-3xl font-extrabold leading-none opacity-70">{value}</p><p className="mt-1.5 text-xs text-muted">{hint}</p></div>
  );
  return (
    <div className="grid grid-cols-2 gap-3 lg:grid-cols-3 xl:grid-cols-6">
      {stat("Done today", p.handledToday, "recorded in the audit log")}
      {stat("Open tasks", p.openTasks, p.overdue ? `${p.overdue} overdue` : "none overdue", p.overdue > 0)}
      {stat("Unread", p.unread, "across connected apps")}
      {stat("Connected", `${p.healthy}/${p.total}`, p.healthy === p.total ? "all healthy" : "some need attention", p.healthy !== p.total)}
      {fake("Time saved", "5h 20m", "sample figure")}
      {fake("Avg reply", "6 min", "sample figure")}
    </div>
  );
}

function FakeIntegrationTiles() {
  const tiles = [{ name: "Calendar", value: "4 events", hint: "1 clash" }, { name: "Payments", value: "1 refund", hint: "awaiting you" }, { name: "Alerts", value: "1 spike", hint: "checkout errors" }];
  return (
    <section aria-label="Integrations not connected yet" className="space-y-2">
      <div className="flex items-center gap-2"><h2 className="text-lg font-bold">More integrations</h2><FakeBadge /><span className="text-xs text-muted">Placeholders until these are connected</span></div>
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
        {tiles.map((t) => (
          <div key={t.name} className={`border-dashed p-4 ${tileClass}`}><div className="flex items-center justify-between"><p className="text-xs font-bold uppercase tracking-wider">{t.name}</p><FakeBadge /></div><p className="mt-1 text-2xl font-extrabold opacity-70">{t.value}</p><p className="text-xs text-muted">{t.hint}</p></div>
        ))}
      </div>
    </section>
  );
}

function FakeAutomations() {
  const rows = [["Reply to trusted contacts on WhatsApp", "AUTO"], ["Sort newsletters out of Gmail", "AUTO"], ["Add invoices to tasks with a reminder", "ASK FIRST"], ["Daily 8 am briefing", "ON"]];
  return (
    <section className={`border-dashed p-4 ${tileClass}`} aria-label="Automations (placeholder)">
      <div className="mb-2 flex items-center gap-2"><h2 className="text-lg font-bold">Automations running</h2><FakeBadge /></div>
      <ul className="divide-y divide-line text-sm">{rows.map(([n, m]) => <li key={n} className="flex items-center justify-between gap-3 py-2 opacity-80"><span>{n}</span><span className="ai-mono rounded-full border border-line px-2.5 py-0.5 text-[11px] font-semibold">{m}</span></li>)}</ul>
    </section>
  );
}

function Activity({ audit }: { audit: AuditEvent[] }) {
  const rows = audit.slice(0, 6);
  return (
    <section className={`p-4 ${tileClass}`} aria-label="Activity and audit">
      <h2 className="mb-2 text-lg font-bold">Activity &amp; audit</h2>
      {rows.length === 0 ? <p className="text-sm text-muted">Nothing recorded yet.</p> : (
        <ul className="divide-y divide-line text-sm">{rows.map((e) => <li key={e.id} className="flex gap-3 py-2"><span className="ai-mono w-12 shrink-0 text-xs text-muted">{new Date(e.occurredAt).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" })}</span><span className="min-w-0 flex-1 truncate">{e.detail ?? e.action}</span></li>)}</ul>
      )}
    </section>
  );
}

function FocusView({ items, busy, onAct }: { items: NeedsItem[]; busy: boolean; onAct: (a: NonNullable<NeedsItem["action"] | NeedsItem["secondary"]>) => void }) {
  const [skipped, setSkipped] = useState<string[]>([]);
  const queue = items.filter((i) => !skipped.includes(i.id));
  const item = queue[0];
  if (!item) return <p className={`p-8 text-center text-sm text-muted ${tileClass}`}>{items.length ? "You've gone through everything. Nice." : "Nothing needs you right now."}</p>;
  const style = SOURCE_STYLE[item.source];
  return (
    <div className={`mx-auto max-w-xl space-y-4 p-6 text-center ${tileClass}`}>
      <p className="ai-mono text-xs text-muted">{items.length - queue.length + 1} of {items.length}</p>
      <span className={`ai-mono inline-block rounded-md px-3 py-1 text-xs font-bold ${style.tone}`}>{style.label}</span>
      <h2 className="text-2xl font-extrabold">{item.title}</h2>
      <p className="text-sm text-muted">{item.detail}</p>
      <div className="flex flex-wrap items-center justify-center gap-2">
        {item.action.kind === "link" ? <Link href={item.action.href} className={btn}>{item.action.label}</Link> : <button type="button" disabled={busy} onClick={() => onAct(item.action)} className={btn}>{item.action.label}</button>}
        {item.secondary && <button type="button" disabled={busy} onClick={() => onAct(item.secondary!)} className={btn2}>{item.secondary.label}</button>}
        <button type="button" onClick={() => setSkipped((s) => [...s, item.id])} className={btn2}>Later</button>
      </div>
    </div>
  );
}

function OpsView({ apps, audit }: { apps: DashboardApp[]; audit: AuditEvent[] }) {
  return (
    <div className="grid gap-4 lg:grid-cols-2">
      <section className={`p-4 ${tileClass}`}>
        <h2 className="mb-2 text-lg font-bold">Connection health</h2>
        <ul className="divide-y divide-line text-sm">
          {apps.map((a) => (
            <li key={a.id} className="flex items-center gap-3 py-2"><span className="min-w-0 flex-1 truncate font-semibold">{a.name}</span><Chip tone={a.status === "Connected" ? "lime" : "pink"}>{a.status}</Chip><span className="ai-mono w-28 text-right text-xs text-muted">{a.lastActiveAt ? new Date(a.lastActiveAt).toLocaleString(undefined, { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" }) : "never"}</span></li>
          ))}
        </ul>
      </section>
      <section className={`p-4 ${tileClass}`}>
        <h2 className="mb-2 text-lg font-bold">Audit log</h2>
        {audit.length === 0 ? <p className="text-sm text-muted">Nothing recorded yet.</p> : (
          <ul className="max-h-80 divide-y divide-line overflow-y-auto text-sm">{audit.slice(0, 30).map((e) => <li key={e.id} className="py-2"><span className="ai-mono mr-2 text-xs text-muted">{new Date(e.occurredAt).toLocaleString(undefined, { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" })}</span><span className="font-semibold">{e.action}</span>{e.detail && <span className="text-muted"> · {e.detail}</span>}</li>)}</ul>
        )}
      </section>
    </div>
  );
}

const VIEWS: { id: DashboardView; label: string }[] = [{ id: "overview", label: "Overview" }, { id: "focus", label: "Focus" }, { id: "ops", label: "Ops" }];
const subscribeNone = () => () => {};
function readView(): DashboardView { try { const v = localStorage.getItem(VIEW_KEY); return isView(v) ? v : "overview"; } catch { return "overview"; } }

/** The dashboard's command band: one place for everything that needs the user, across every connected app. */
export function CommandCentre({ apps }: { apps: DashboardApp[] }) {
  const { state, reload } = useApi(() => loadAll(apps));
  const stored = useSyncExternalStore(subscribeNone, readView, () => "overview" as DashboardView);
  const [view, setView] = useState<DashboardView | null>(null);
  const current = view ?? stored;
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState("");
  const [last, setLast] = useState<Data | null>(null);
  const [seen, setSeen] = useState<unknown>(null);
  if (state.status === "ready" && state.data !== seen) { setSeen(state.data); setLast(state.data); }
  const data = last;

  function pick(v: DashboardView) { setView(v); try { localStorage.setItem(VIEW_KEY, v); } catch { /* storage unavailable */ } }

  async function act(a: NonNullable<NeedsItem["action"] | NeedsItem["secondary"]>) {
    if (a.kind === "link") return;
    setBusy(true); setActionError("");
    try {
      if (a.kind === "approve-reply") await api.approveWhatsAppPendingReply(a.connectionId, a.replyId);
      else if (a.kind === "discard-reply") await api.discardWhatsAppPendingReply(a.connectionId, a.replyId);
      else await api.completeTask(a.taskId);
      reload();
    } catch (e) { setActionError(e instanceof Error ? e.message : "That did not work. Try again."); }
    finally { setBusy(false); }
  }

  const needs = data ? buildNeedsYou({ pending: data.pending, tasks: data.tasks, apps }) : [];
  const next = data ? nextUp(data.tasks, data.reminders) : null;
  const today = data ? buildToday(data.tasks, data.reminders) : [];

  return (
    <section aria-label="Command Centre" className="mb-4 space-y-4">
      <ThemeHero apps={apps} stats={data ? { needs: needs.length, handled: buildPulse({ audit: data.audit, tasks: data.tasks, apps }).handledToday, next: next?.label ?? null, first: needs[0]?.title ?? null } : null} />
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-xl font-extrabold">Command Centre</h2>
        <div role="radiogroup" aria-label="Dashboard view" className="inline-flex rounded-full border border-line bg-surface p-1">
          {VIEWS.map((v) => <button key={v.id} type="button" role="radio" aria-checked={current === v.id} onClick={() => pick(v.id)} className={`rounded-full px-4 py-1.5 text-xs font-semibold ${current === v.id ? "bg-accent text-accent-contrast" : "text-muted hover:text-text"}`}>{v.label}</button>)}
        </div>
      </div>

      <AskBar />
      <FakeSearch />

      {state.status === "error" && !data && <p role="alert" className="rounded-2xl bg-danger-bg p-4 text-sm text-danger">Could not load the Command Centre. <button type="button" onClick={reload} className="font-semibold underline">Try again</button></p>}
      {!data && state.status === "loading" && <p role="status" className="text-sm text-muted">Loading your day…</p>}
      {actionError && <p role="alert" className="rounded-2xl bg-danger-bg px-4 py-2 text-sm text-danger">{actionError}</p>}

      {data && current === "overview" && (
        <>
          <div className="grid gap-4 lg:grid-cols-12">
            <section className={`p-4 lg:col-span-8 ${tileClass}`} aria-label="Needs you">
              <div className="flex items-baseline justify-between"><h2 className="text-lg font-bold">Needs you <span className="ai-mono text-sm text-accent">{needs.length}</span></h2><span className="text-xs text-muted">One list for every app</span></div>
              {needs.length === 0 ? <p className="py-6 text-center text-sm text-muted">Nothing needs you right now.</p> : <ul className="divide-y divide-line">{needs.map((i) => <NeedsRow key={i.id} item={i} busy={busy} onAct={act} />)}</ul>}
            </section>
            <div className="flex flex-col gap-4 lg:col-span-4">
              <section className="rounded-[1.25rem] bg-accent p-4 text-accent-contrast" aria-label="Next up">
                <p className="ai-mono text-[11px] font-semibold uppercase tracking-wider opacity-80">Next up{next ? ` · ${relativeTime(next.at)}` : ""}</p>
                <p className="mt-1 text-xl font-extrabold">{next ? next.label : "Nothing scheduled"}</p>
                {next && <p className="text-sm opacity-80">{next.at.toLocaleString(undefined, { weekday: "short", hour: "2-digit", minute: "2-digit" })}</p>}
              </section>
              <section className={`flex-1 p-4 ${tileClass}`} aria-label="Today">
                <p className="ai-mono mb-2 text-[11px] font-semibold uppercase tracking-wider text-muted">Today</p>
                {today.length === 0 ? <p className="text-sm text-muted">Nothing more today.</p> : <ul className="space-y-1.5 text-sm">{today.map((t) => <li key={t.id} className="flex gap-3"><span className="ai-mono w-12 shrink-0 text-muted">{t.at.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" })}</span><span className="min-w-0 flex-1 truncate">{t.label}</span></li>)}</ul>}
              </section>
            </div>
          </div>
          <Pulse data={data} apps={apps} />
          <FakeIntegrationTiles />
          <div className="grid gap-4 lg:grid-cols-2"><FakeAutomations /><Activity audit={data.audit} /></div>
        </>
      )}
      {data && current === "focus" && <FocusView items={needs} busy={busy} onAct={act} />}
      {data && current === "ops" && <OpsView apps={apps} audit={data.audit} />}
    </section>
  );
}
