"use client";
import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { api, type DashboardApp, type WhatsAppInsight, type WhatsAppPendingReply } from "@/lib/api";
import { getIntegrationsHub } from "@/lib/signalr";
import { tileClass } from "@/components/ui";

export type Overview = {
  chats: number;
  unread: number;
  tasks: WhatsAppInsight[];
  reminders: WhatsAppInsight[];
  pending: WhatsAppPendingReply[];
  topChats: { chatId: string; name: string; unread: number; preview: string }[];
};

const EMPTY: Overview = { chats: 0, unread: 0, tasks: [], reminders: [], pending: [], topChats: [] };

/** Everything the dashboard summarises for one connected WhatsApp account; refreshes on hub events + a slow poll. */
export function useWhatsAppOverview(connectionId: string | null): [Overview, () => void] {
  const [data, setData] = useState<Overview>(EMPTY);

  const load = useCallback(async () => {
    if (!connectionId) return;
    const [chats, insights, pending] = await Promise.all([
      api.whatsAppChats(connectionId).catch(() => []),
      api.whatsAppInsights(connectionId).catch(() => []),
      api.whatsAppPendingReplies(connectionId).catch(() => []),
    ]);
    const open = insights.filter((i) => !i.done);
    setData({
      chats: chats.length,
      unread: chats.reduce((n, c) => n + c.unreadCount, 0),
      tasks: open.filter((i) => i.kind.toLowerCase() === "task"),
      reminders: open.filter((i) => i.kind.toLowerCase() === "reminder"),
      pending,
      topChats: chats
        .filter((c) => c.unreadCount > 0)
        .slice(0, 4)
        .map((c) => ({ chatId: c.chatId, name: c.chatName ?? c.chatId.split("@")[0], unread: c.unreadCount, preview: c.lastMessagePreview })),
    });
  }, [connectionId]);

  useEffect(() => {
    const first = setTimeout(() => void load(), 0);
    const poll = setInterval(() => void load(), 15000);
    const hub = getIntegrationsHub();
    const refresh = () => void load();
    for (const e of ["WhatsAppMessageReceived", "WhatsAppMessageSent", "WhatsAppMessageUpdated", "WhatsAppReminderDue"]) hub.on(e, refresh);
    return () => {
      clearTimeout(first);
      clearInterval(poll);
      for (const e of ["WhatsAppMessageReceived", "WhatsAppMessageSent", "WhatsAppMessageUpdated", "WhatsAppReminderDue"]) hub.off(e, refresh);
    };
  }, [load]);

  return [data, () => void load()];
}

function greeting(): string {
  const h = new Date().getHours();
  return h < 5 ? "Working late" : h < 12 ? "Good morning" : h < 17 ? "Good afternoon" : "Good evening";
}

export function DashboardHero({ apps, canResetLayout = false, onResetLayout, layoutSaveFailed = false, onRetryLayoutSave }: {
  apps: DashboardApp[];
  /** True when tiles have been moved or resized, so "Reset layout" would do something. */
  canResetLayout?: boolean;
  onResetLayout?: () => void;
  /** The last attempt to save the arrangement failed. */
  layoutSaveFailed?: boolean;
  onRetryLayoutSave?: () => void;
}) {
  const connected = apps.filter((a) => a.status === "Connected").length;
  const unread = apps.reduce((n, a) => n + a.unreadCount, 0);
  const bits = [`${apps.length} on your dashboard`, unread > 0 ? `${unread} unread message${unread === 1 ? "" : "s"}` : "inbox is clear"];
  return (
    <section className="relative mb-4 overflow-hidden rounded-[1.25rem] border border-line bg-lime p-6 text-on-tint shadow-[var(--shadow)] sm:p-8">
      <div aria-hidden className="pointer-events-none absolute -right-10 -top-16 h-56 w-56 rounded-full bg-pink/70 blur-2xl" />
      <div aria-hidden className="pointer-events-none absolute -bottom-20 right-24 h-48 w-48 rounded-full bg-white/40 blur-2xl" />
      <div className="relative flex flex-wrap items-end justify-between gap-4">
        <div>
          <p className="ai-mono text-xs font-semibold uppercase tracking-widest opacity-70">
            {new Date().toLocaleDateString(undefined, { weekday: "long", day: "numeric", month: "long" })}
          </p>
          <h1 className="mt-1 text-3xl font-extrabold sm:text-4xl">{greeting()}.</h1>
          <p className="mt-1 max-w-xl text-sm font-medium opacity-80">
            {connected} connected · {bits.join(" · ")}
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {layoutSaveFailed && (
            <button type="button" onClick={onRetryLayoutSave} className="rounded-full border border-black/40 bg-white/60 px-5 py-2 text-sm font-semibold text-black hover:bg-white/80">
              Couldn&apos;t save layout — retry
            </button>
          )}
          {canResetLayout && (
            <button type="button" onClick={onResetLayout} className="rounded-full border border-black/40 px-5 py-2 text-sm font-semibold text-black hover:bg-black/10">
              Reset layout
            </button>
          )}
          <Link href="/apps" className="rounded-full bg-black/85 px-5 py-2 text-sm font-semibold text-white hover:bg-black">
            Add or manage apps
          </Link>
        </div>
      </div>
    </section>
  );
}

function Stat({ label, value, hint, tone }: { label: string; value: number | string; hint: string; tone?: "lime" | "pink" }) {
  const toneClass = tone === "lime" ? "bg-lime text-on-tint" : tone === "pink" ? "bg-pink text-on-tint" : `bg-surface ${""}`;
  return (
    <div className={`rounded-[1.25rem] border border-line p-4 shadow-[var(--shadow)] ${toneClass}`}>
      <p className="ai-mono text-[11px] font-semibold uppercase tracking-wider opacity-70">{label}</p>
      <p className="mt-1 text-3xl font-extrabold leading-none">{value}</p>
      <p className="mt-1.5 text-xs opacity-70">{hint}</p>
    </div>
  );
}

export function StatTiles({ overview }: { overview: Overview }) {
  return (
    <div className="mb-4 grid grid-cols-2 gap-3 lg:grid-cols-4">
      <Stat label="Unread" value={overview.unread} hint={overview.unread ? "waiting for you" : "all caught up"} tone={overview.unread ? "pink" : undefined} />
      <Stat label="Chats" value={overview.chats} hint="on WhatsApp" />
      <Stat label="Open tasks" value={overview.tasks.length} hint="picked up from messages" />
      <Stat label="Reminders" value={overview.reminders.length} hint={overview.pending.length ? `${overview.pending.length} reply awaiting you` : "upcoming"} tone={overview.reminders.length ? "lime" : undefined} />
    </div>
  );
}

function SideCard({ title, count, children }: { title: string; count?: number; children: React.ReactNode }) {
  return (
    <section className={`p-4 ${tileClass}`}>
      <div className="mb-2 flex items-center justify-between">
        <h2 className="text-sm font-extrabold">{title}</h2>
        {count !== undefined && count > 0 && <span className="rounded-full bg-surface-2 px-2 py-0.5 text-xs font-semibold text-muted">{count}</span>}
      </div>
      {children}
    </section>
  );
}

export function AttentionSidebar({ connectionId, overview, onChanged }: { connectionId: string; overview: Overview; onChanged: () => void }) {
  const act = async (fn: () => Promise<void>) => {
    try {
      await fn();
    } finally {
      onChanged();
    }
  };
  const items = [...overview.reminders, ...overview.tasks].slice(0, 6);
  return (
    <div className="space-y-3">
      {overview.pending.length > 0 && (
        <SideCard title="Replies awaiting approval" count={overview.pending.length}>
          <ul className="space-y-2">
            {overview.pending.slice(0, 3).map((p) => (
              <li key={p.id} className="rounded-xl bg-surface-2 p-3 text-sm">
                <p className="text-xs font-semibold text-muted">To {p.chatName ?? p.chatId.split("@")[0]}</p>
                <p className="mt-0.5 line-clamp-3">{p.text}</p>
                <div className="mt-2 flex gap-2">
                  <button onClick={() => void act(() => api.approveWhatsAppPendingReply(connectionId, p.id))} className="rounded-full bg-accent px-3 py-1 text-xs font-semibold text-accent-contrast">Send</button>
                  <button onClick={() => void act(() => api.discardWhatsAppPendingReply(connectionId, p.id))} className="rounded-full border border-line px-3 py-1 text-xs font-semibold text-muted hover:text-text">Discard</button>
                </div>
              </li>
            ))}
          </ul>
        </SideCard>
      )}

      <SideCard title="Needs your attention" count={items.length}>
        {items.length === 0 ? (
          <p className="text-sm text-muted">No open tasks or reminders. Daksha adds them as it spots them in your messages.</p>
        ) : (
          <ul className="space-y-1.5">
            {items.map((i) => (
              <li key={i.id} className="flex items-start gap-2.5 rounded-xl px-1.5 py-1.5 hover:bg-surface-2">
                <button
                  aria-label="Mark done"
                  onClick={() => void act(() => api.toggleWhatsAppInsightDone(connectionId, i.id))}
                  className="mt-0.5 h-4.5 w-4.5 shrink-0 rounded-full border-2 border-muted hover:border-accent"
                />
                <div className="min-w-0 text-sm">
                  <p className="line-clamp-2">{i.text}</p>
                  <p className="text-[11px] text-muted">
                    <span className="uppercase">{i.kind}</span> · {i.chatName ?? "chat"}
                    {i.dueAt ? ` · due ${new Date(i.dueAt).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" })}` : ""}
                  </p>
                </div>
              </li>
            ))}
          </ul>
        )}
      </SideCard>

      {overview.topChats.length > 0 && (
        <SideCard title="Unread chats" count={overview.unread}>
          <ul className="space-y-1">
            {overview.topChats.map((c) => (
              <li key={c.chatId} className="flex items-center gap-3 rounded-xl px-1.5 py-1.5">
                <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-pink text-sm font-extrabold text-on-tint">{c.name.charAt(0).toUpperCase()}</span>
                <div className="min-w-0 flex-1 text-sm">
                  <p className="truncate font-semibold">{c.name}</p>
                  <p className="truncate text-xs text-muted">{c.preview}</p>
                </div>
                <span className="rounded-full bg-accent px-2 py-0.5 text-[11px] font-bold text-accent-contrast">{c.unread}</span>
              </li>
            ))}
          </ul>
        </SideCard>
      )}
    </div>
  );
}
