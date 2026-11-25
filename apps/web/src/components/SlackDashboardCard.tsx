"use client";
import Link from "next/link";
import { useState } from "react";
import { BrandTile } from "@/components/integrations";
import { SlackManagePanel } from "@/components/SlackManagePanel";
import { Modal } from "@/components/Modal";
import { ErrorState, Loading, tileClass } from "@/components/ui";
import { listDate } from "@/components/GmailMailbox";
import { api, type DashboardApp } from "@/lib/api";
import { useApi } from "@/lib/useApi";

/** The Slack card on the Daksha dashboard: your latest direct messages and mentions, or the people and channels you talk to. */
export function SlackDashboardCard({ app, onChanged }: { app: DashboardApp; onChanged?: () => void }) {
  const [manageOpen, setManageOpen] = useState(false);
  const { state, reload } = useApi(() => api.slackDashboard(app.connectionId));

  return (
    <div className={`min-h-full p-4 ${tileClass}`}>
      <header className="flex items-start gap-3">
        <BrandTile icon={app.icon} color={app.color} size={40} />
        <div className="min-w-0 flex-1">
          <h2 className="text-xl font-bold leading-tight">Slack{state.status === "ready" && state.data.unread > 0 ? <span className="ml-2 rounded-full bg-accent px-2 py-0.5 align-middle text-xs font-bold text-accent-contrast">{state.data.unread} new</span> : null}</h2>
          <p className="truncate text-xs text-muted sm:text-sm">{app.accountIdentifier ?? "Direct messages and mentions"}</p>
        </div>
        <Link href={`/apps/slack?workspace=${encodeURIComponent(app.connectionId)}`} className="self-center text-xs font-semibold text-accent hover:underline">Open</Link>
        <button type="button" onClick={() => setManageOpen(true)} aria-label="Manage Slack" className="rounded-xl border border-line p-2 text-muted hover:bg-surface-2">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" className="h-4.5 w-4.5"><path d="M12 15a3 3 0 100-6 3 3 0 000 6zM19 12l2-1-2-4-2 .5-1.5-1L15 4h-4l-.5 2.5-1.5 1L7 7l-2 4 2 1v2l-2 1 2 4 2-.5 1.5 1L11 20h4l.5-2.5 1.5-1 2 .5 2-4-2-1z" /></svg>
        </button>
      </header>

      <div className="mt-3">
        {state.status === "loading" && <Loading label="Loading Slack" />}
        {state.status === "error" && <ErrorState error={state.error} onRetry={reload} />}
        {state.status === "ready" && (
          <div className="space-y-2">
            {state.data.errors.map((e) => <p key={e} role="alert" className="text-xs text-danger">{e}</p>)}
            {state.data.activity.length > 0 ? (
              <ul className="space-y-1.5">{state.data.activity.map((a) => (
                <li key={a.id} className="min-w-0 text-sm">
                  <span className={`block truncate ${a.read ? "" : "font-bold"}`}>{a.userName} <span className="font-normal text-muted">· {a.kind === "dm" ? "direct message" : "mention"}</span></span>
                  <span className="block truncate text-xs text-muted">{a.text} · {listDate(a.time)}</span>
                </li>))}</ul>
            ) : state.data.conversations.length > 0 ? (
              <ul className="space-y-1">{state.data.conversations.map((c) => (
                <li key={c.id} className="truncate text-sm">{c.kind === "dm" ? "💬 " : c.kind === "group" ? "👥 " : "# "}{c.name}</li>))}</ul>
            ) : <p className="text-xs text-muted">{state.data.eventsConfigured ? "No new direct messages or mentions." : "Nothing to show."}</p>}
          </div>
        )}
      </div>

      <Modal wide fixedHeight open={manageOpen} onClose={() => { setManageOpen(false); reload(); }}>
        <SlackManagePanel onChanged={() => { reload(); onChanged?.(); }} />
      </Modal>
    </div>
  );
}
