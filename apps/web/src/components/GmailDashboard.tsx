"use client";
import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { BrandTile } from "@/components/integrations";
import { Avatar, listDate, parseSender } from "@/components/GmailMailbox";
import { ApiError } from "@/lib/api";
import { gmailControl, type DashboardPriority, type GmailDashboardData } from "@/lib/gmailControl";

export type MailboxTarget = { query?: string; compose?: boolean; focusSearch?: boolean; openMessageId?: string };
export type ManageTab = "followups" | "rules" | "security";

const REFRESH_MS = 60_000;

function Icon({ path, className = "h-5 w-5" }: { path: string; className?: string }) {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" className={className} aria-hidden>
      <path d={path} />
    </svg>
  );
}
const ICONS = {
  mail: "M3 6h18v12H3zM3 7l9 7 9-7",
  open: "M3 10l9-6 9 6v9H3zM3 10l9 6 9-6",
  flag: "M5 21V4M5 4h11l-2 4 2 4H5",
  reply: "M9 8L4 13l5 5M4 13h10a6 6 0 016 6",
  star: "M12 3l2.8 5.7 6.2.9-4.5 4.4 1.1 6.2L12 17.2 6.4 20.2l1.1-6.2L3 9.6l6.2-.9z",
  compose: "M4 20l4-1L19 8l-3-3L5 16zM14 7l3 3",
  search: "M11 4a7 7 0 100 14 7 7 0 000-14zM21 21l-5-5",
  clock: "M12 4a8 8 0 100 16 8 8 0 000-16zM12 8v4l3 2",
  bolt: "M13 3L5 14h6l-1 7 8-11h-6z",
  settings: "M12 15a3 3 0 100-6 3 3 0 000 6zM19 12l2-1-2-4-2 .5-1.5-1L15 4h-4l-.5 2.5-1.5 1L7 7l-2 4 2 1v2l-2 1 2 4 2-.5 1.5 1L11 20h4l.5-2.5 1.5-1 2 .5 2-4-2-1z",
  arrow: "M5 12h14M13 6l6 6-6 6",
  refresh: "M20 11a8 8 0 10-2.3 5.7M20 4v7h-7",
  doc: "M7 3h7l5 5v13H7zM14 3v5h5M10 13h6M10 17h6",
  target: "M12 4a8 8 0 100 16 8 8 0 000-16zM12 8a4 4 0 100 8 4 4 0 000-8zM12 12h.01",
  chart: "M5 20V10M12 20V4M19 20v-7",
} as const;

const BADGE: Record<string, string> = {
  "Action Required": "bg-danger-bg text-danger",
  Payment: "bg-accent/15 text-accent",
  Meeting: "bg-lime text-on-tint",
  Interview: "bg-pink text-on-tint",
  Important: "bg-surface-2 text-text",
};

function trend(today: number, yesterday: number): { text: string; up: boolean } | null {
  if (yesterday === 0) return null;
  const pct = Math.round(((today - yesterday) / yesterday) * 100);
  return pct === 0 ? null : { text: `${Math.abs(pct)}% vs yesterday`, up: pct > 0 };
}

function Stat({ icon, tone, value, label, sub, hint }: { icon: string; tone: string; value: number | null; label: string; sub?: ReactNode; hint?: string }) {
  return (
    <li className="min-w-0 rounded-2xl border border-line bg-surface p-3 shadow-[var(--shadow)]" title={value === null ? hint : undefined}>
      <span className={`flex h-8 w-8 items-center justify-center rounded-lg ${tone}`}><Icon path={icon} className="h-4 w-4" /></span>
      <p className="mt-2 text-2xl font-bold leading-none" aria-label={`${label}: ${value ?? "not available yet"}`}>{value === null ? "—" : value.toLocaleString()}</p>
      <p className="mt-1 text-xs font-semibold text-muted">{label}</p>
      {sub && <p className="mt-0.5 text-[11px] text-muted">{sub}</p>}
    </li>
  );
}

/**
 * The Gmail landing view: a compact, data-driven summary. Every number comes from the connected account (Gmail counts) or from what Daksha
 * has synced and analysed (importance, responses needed, deadlines). Nothing here is sample data.
 */
export function GmailDashboard({ icon, color, accountId, accountEmail, onOpenMailbox, onOpenManage }: {
  icon: string; color: string; /** Which Gmail account this card shows; without it, the default account. */ accountId?: string; accountEmail?: string | null; onOpenMailbox: (t?: MailboxTarget) => void; onOpenManage: (tab?: ManageTab) => void;
}) {
  const [data, setData] = useState<GmailDashboardData | null>(null);
  const [error, setError] = useState<ApiError | Error | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [briefingOpen, setBriefingOpen] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const alive = useRef(true);

  const fetchNow = useCallback((fresh = false) =>
    gmailControl.dashboard(accountId, fresh).then(
      (next) => { if (alive.current) { setData(next); setError(null); } },
      (e) => { if (alive.current) setError(e instanceof Error ? e : new Error("Could not load the dashboard.")); },
    ), [accountId]);

  const refresh = useCallback(() => { setRefreshing(true); void fetchNow(true).finally(() => { if (alive.current) setRefreshing(false); }); }, [fetchNow]);

  useEffect(() => {
    alive.current = true;
    void fetchNow(); // the first load and the 60 s refresh only set state after the request resolves
    const timer = window.setInterval(() => void fetchNow(), REFRESH_MS);
    return () => { alive.current = false; window.clearInterval(timer); };
  }, [fetchNow]);

  async function toggleStar(row: DashboardPriority) {
    setNotice(null);
    const next = !row.starred;
    setData((d) => d && { ...d, priority: d.priority.map((p) => (p.id === row.id ? { ...p, starred: next } : p)) });
    try { await gmailControl.messageAction(row.id, next ? "star" : "unstar", accountId); }
    catch (e) {
      setData((d) => d && { ...d, priority: d.priority.map((p) => (p.id === row.id ? { ...p, starred: row.starred } : p)) });
      setNotice(e instanceof ApiError && (e.code === "scope_upgrade_required" || e.code === "permission_denied")
        ? `${e.message} Open Settings → Security to allow it.` : "Could not update the star. Please try again.");
    }
  }

  const denied = error instanceof ApiError && (error.code === "permission_denied" || error.code === "scope_upgrade_required");
  const syncing = data?.sync.status === "Synchronising";
  const syncFailed = data?.sync.status === "Failed";
  const status = syncFailed ? { text: "Sync issue", cls: "bg-danger-bg text-danger" } : syncing ? { text: "Syncing", cls: "bg-surface-2 text-text" } : { text: "Connected", cls: "bg-lime text-on-tint" };

  return (
    <section aria-label="Gmail dashboard" aria-busy={!data && !error} className="relative min-w-0 space-y-3">
      {/* Working effect across the ENTIRE card (first load and every refresh), not just the header. */}
      {(refreshing || (!data && !error)) && <CardBusyOverlay />}
      <header className="flex items-start gap-3">
        <BrandTile icon={icon} color={color} size={40} />
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <h2 className="text-xl font-bold leading-tight">Gmail Dashboard</h2>
            {accountEmail && <span className="truncate text-sm font-semibold text-muted">{accountEmail}</span>}
            <span className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-0.5 text-xs font-semibold ${status.cls}`}><span aria-hidden className="h-1.5 w-1.5 rounded-full bg-current" />{status.text}</span>
          </div>
          <p className="mt-0.5 text-xs text-muted sm:text-sm">Manage, summarize and take action on your Gmail with Daksha.</p>
        </div>
        <button onClick={refresh} aria-label="Refresh dashboard" disabled={refreshing} className="rounded-full p-2 text-muted hover:bg-surface-2 hover:text-text disabled:opacity-50"><Icon path={ICONS.refresh} className="h-4.5 w-4.5" /></button>
        <button onClick={() => onOpenManage()} aria-label="Gmail settings" className="rounded-xl border border-line p-2 text-muted hover:bg-surface-2 hover:text-text"><Icon path={ICONS.settings} className="h-4.5 w-4.5" /></button>
      </header>

      {error && !data && (
        <div role="alert" className="rounded-2xl bg-danger-bg p-3 text-sm text-danger">
          <p>{denied ? error.message : error instanceof ApiError ? error.message : "Could not load your Gmail dashboard."}</p>
          <div className="mt-2 flex gap-2">
            <button onClick={refresh} className="rounded-full border border-current px-3 py-1 text-xs font-semibold">Try again</button>
            {denied && <button onClick={() => onOpenManage("security")} className="rounded-full border border-current px-3 py-1 text-xs font-semibold">Open permissions</button>}
          </div>
        </div>
      )}
      {!data && !error && <DashboardSkeleton />}
      {data && error && <p role="alert" className="rounded-2xl bg-danger-bg p-2.5 text-xs text-danger">Showing the last data we loaded — {error.message}</p>}
      {data && syncFailed && data.sync.lastError && <p role="alert" className="rounded-2xl bg-danger-bg p-2.5 text-xs text-danger">Sync problem: {data.sync.lastError} <button className="font-semibold underline" onClick={() => onOpenManage("security")}>Review permissions</button></p>}
      {notice && <p role="status" className="rounded-2xl bg-surface-2 p-2.5 text-xs">{notice}</p>}

      {data && (
        <>
          <Briefing data={data} open={briefingOpen} onToggle={() => setBriefingOpen((v) => !v)} onOpenMessage={(id) => onOpenMailbox({ openMessageId: id })} />

          <ul className="grid grid-cols-2 gap-2 sm:grid-cols-4" aria-label="Email statistics">
            <Stat icon={ICONS.mail} tone="bg-accent/15 text-accent" value={data.counts.today} label="Total Emails" sub={
              (() => { const t = trend(data.counts.today, data.counts.yesterday); return t ? <span className={t.up ? "text-accent" : "text-muted"}>{t.up ? "↑" : "↓"} {t.text}</span> : "Today"; })()
            } />
            <Stat icon={ICONS.open} tone="bg-accent/15 text-accent" value={data.counts.unread} label="Unread" sub="In inbox" />
            <Stat icon={ICONS.flag} tone="bg-pink text-on-tint" value={data.counts.important} label="Important" sub="Today" />
            <Stat icon={ICONS.reply} tone="bg-danger-bg text-danger" value={data.counts.needResponse} label="Need Response" hint="Available once Daksha finishes syncing this account." sub={data.counts.needResponse === null ? "After first sync" : undefined} />
          </ul>

          <section aria-label="Priority emails" className="min-h-[19.5rem] rounded-2xl border border-line bg-surface shadow-[var(--shadow)]">
            <div className="flex items-center justify-between px-3 pt-3">
              <h3 className="text-sm font-bold">Priority Emails</h3>
              <button onClick={() => onOpenMailbox({ query: "in:inbox is:important" })} className="inline-flex items-center gap-1 text-xs font-semibold hover:text-accent">View All <Icon path={ICONS.arrow} className="h-3.5 w-3.5" /></button>
            </div>
            {data.priority.length === 0 ? (
              <p className="px-3 py-12 text-center text-sm text-muted">No important emails right now.{!data.sync.initialSyncDone && " Daksha is still analysing your inbox."}</p>
            ) : (
              <ul className="mt-1 divide-y divide-line">
                {data.priority.map((m) => {
                  const sender = parseSender(m.from);
                  return (
                    <li key={m.id} className="flex items-center gap-2 px-3 py-2">
                      <button onClick={() => onOpenMailbox({ openMessageId: m.id })} className="flex min-w-0 flex-1 items-center gap-2.5 text-left" aria-label={`Open email: ${m.subject || "no subject"} from ${sender.name}`}>
                        <Avatar name={sender.name} size={34} />
                        <span className="min-w-0 flex-1">
                          <span className={`block truncate text-sm ${m.unread ? "font-bold" : "font-semibold"}`}>{m.subject || "(no subject)"}</span>
                          <span className="block truncate text-xs text-muted">{sender.name}{m.date ? ` · ${listDate(m.date)}` : ""}</span>
                        </span>
                        <span className={`hidden shrink-0 rounded-full px-2.5 py-0.5 text-[11px] font-semibold sm:inline ${BADGE[m.label] ?? BADGE.Important}`}>{m.label}</span>
                      </button>
                      <button onClick={() => void toggleStar(m)} aria-label={m.starred ? "Remove star" : "Star this email"} aria-pressed={m.starred} className={`shrink-0 rounded-full p-1.5 hover:bg-surface-2 ${m.starred ? "text-[#e37400]" : "text-muted"}`}>
                        <svg viewBox="0 0 24 24" className="h-4.5 w-4.5" fill={m.starred ? "currentColor" : "none"} stroke="currentColor" strokeWidth="1.8" strokeLinejoin="round" aria-hidden><path d={ICONS.star} /></svg>
                      </button>
                    </li>
                  );
                })}
              </ul>
            )}
          </section>

          <section aria-label="Quick actions">
            <h3 className="mb-1.5 text-sm font-bold">Quick Actions</h3>
            <ul className="grid grid-cols-2 gap-2 sm:grid-cols-4">
              {[
                { label: "Compose", icon: ICONS.compose, run: () => onOpenMailbox({ compose: true }) },
                { label: "Search", icon: ICONS.search, run: () => onOpenMailbox({ focusSearch: true }) },
                { label: "Follow-ups", icon: ICONS.clock, run: () => onOpenManage("followups") },
                { label: "Automations", icon: ICONS.bolt, run: () => onOpenManage("rules") },
              ].map((a) => (
                <li key={a.label}>
                  <button onClick={a.run} className="flex w-full flex-col items-center gap-1.5 rounded-2xl border border-line bg-surface px-2 py-3 text-xs font-semibold shadow-[var(--shadow)] transition-colors hover:border-accent hover:text-accent">
                    <Icon path={a.icon} className="h-5 w-5 text-accent" />{a.label}
                  </button>
                </li>
              ))}
            </ul>
          </section>
        </>
      )}
    </section>
  );
}

function Briefing({ data, open, onToggle, onOpenMessage }: { data: GmailDashboardData; open: boolean; onToggle: () => void; onOpenMessage: (id: string) => void }) {
  const c = data.counts;
  const bold = (n: number | string) => <strong className="font-bold text-text">{n}</strong>;
  return (
    <section aria-label="AI inbox briefing" className="rounded-2xl border border-accent/30 bg-accent/10 p-3">
      <div className="flex items-start gap-3">
        <span className="mt-0.5 text-accent"><Icon path="M12 3l1.8 5.2L19 10l-5.2 1.8L12 17l-1.8-5.2L5 10l5.2-1.8zM19 16l.7 1.8L21.5 18.5l-1.8.7L19 21l-.7-1.8-1.8-.7 1.8-.7z" className="h-6 w-6" /></span>
        <div className="min-w-0 flex-1">
          <p className="flex items-center gap-2 text-sm font-bold">AI Inbox Briefing <span className="rounded-full bg-surface px-2 py-0.5 text-[11px] font-semibold text-muted">Today</span></p>
          <p className="mt-1 text-xs leading-relaxed text-muted">
            You received {bold(c.today)} email{c.today === 1 ? "" : "s"}. {bold(c.important)} {c.important === 1 ? "is" : "are"} important
            {c.needResponse !== null && c.deadlines !== null
              ? <>, {bold(c.needResponse)} need{c.needResponse === 1 ? "s" : ""} your response, and {bold(c.deadlines)} contain{c.deadlines === 1 ? "s" : ""} upcoming deadlines.</>
              : <>. Responses and deadlines will appear once Daksha finishes analysing your inbox.</>}
            {!data.aiEnabled && " AI analysis is turned off for this account."}
          </p>
        </div>
        <button onClick={onToggle} aria-expanded={open} className="shrink-0 rounded-full border border-accent px-3 py-1.5 text-xs font-semibold text-accent hover:bg-accent/10">
          {open ? "Hide Briefing" : "View Briefing"} <Icon path={ICONS.arrow} className="ml-0.5 inline h-3.5 w-3.5" />
        </button>
      </div>
      {open && (
        <div className="mt-3 grid gap-3 border-t border-accent/20 pt-3 sm:grid-cols-2">
          <BriefingList title="Needs your response" empty="Nothing is waiting on you." items={data.briefing.needResponse.map((n) => ({ id: n.messageId, title: n.subject || "(no subject)", sub: parseSender(n.from).name }))} onOpen={onOpenMessage} />
          <BriefingList title="Upcoming deadlines" empty="No deadlines found in the next week." items={data.briefing.deadlines.map((d) => ({ id: d.messageId, title: d.subject || "(no subject)", sub: d.next ? `Due ${new Date(d.next).toLocaleDateString([], { month: "short", day: "numeric" })}` : parseSender(d.from).name }))} onOpen={onOpenMessage} />
        </div>
      )}
    </section>
  );
}

function BriefingList({ title, items, empty, onOpen }: { title: string; items: { id: string; title: string; sub: string }[]; empty: string; onOpen: (id: string) => void }) {
  return (
    <div className="min-w-0">
      <p className="text-xs font-bold">{title}</p>
      {items.length === 0 ? <p className="mt-1 text-xs text-muted">{empty}</p> : (
        <ul className="mt-1 space-y-1">
          {items.map((i) => (
            <li key={i.id}><button onClick={() => onOpen(i.id)} className="block w-full min-w-0 rounded-lg px-2 py-1 text-left hover:bg-surface"><span className="block truncate text-xs font-semibold">{i.title}</span><span className="block truncate text-[11px] text-muted">{i.sub}</span></button></li>
          ))}
        </ul>
      )}
    </div>
  );
}

/** Shaped like the loaded dashboard (briefing, four stats, five rows, four actions) so the card never changes size when data arrives. */
/** Covers the whole card — including its padding — with the sweep effect. The parent card must be `relative` or this section is. */
export function CardBusyOverlay() {
  return <div aria-hidden data-card-busy className="card-busy pointer-events-none absolute -inset-4 z-20 rounded-[1.25rem]" />;
}

export function DashboardSkeleton() {
  return (
    <div role="status" aria-label="Loading your Gmail dashboard" className="space-y-3">
      <div className="skeleton h-[5.25rem] rounded-2xl" />
      <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
        {[0, 1, 2, 3].map((i) => <div key={i} className="skeleton h-[6.5rem] rounded-2xl" />)}
      </div>
      <div className="min-h-[19.5rem] rounded-2xl border border-line bg-surface p-3">
        <div className="skeleton h-4 w-28 rounded" />
        <ul className="mt-3 space-y-3">
          {[0, 1, 2, 3, 4].map((i) => (
            <li key={i} className="flex items-center gap-2.5">
              <span className="skeleton h-[34px] w-[34px] shrink-0 rounded-full" />
              <span className="min-w-0 flex-1 space-y-1.5"><span className="skeleton block h-3 w-2/3 rounded" /><span className="skeleton block h-2.5 w-1/3 rounded" /></span>
              <span className="skeleton hidden h-5 w-20 rounded-full sm:block" />
            </li>
          ))}
        </ul>
      </div>
      <div>
        <div className="skeleton mb-1.5 h-4 w-24 rounded" />
        <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">{[0, 1, 2, 3].map((i) => <div key={i} className="skeleton h-[4.25rem] rounded-2xl" />)}</div>
      </div>
    </div>
  );
}
