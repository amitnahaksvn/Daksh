"use client";
import Link from "next/link";
import { useState } from "react";
import { BrandTile } from "@/components/integrations";
import { MicrosoftManagePanel } from "@/components/MicrosoftManagePanel";
import { Modal } from "@/components/Modal";
import { ErrorState, Loading, tileClass } from "@/components/ui";
import { listDate } from "@/components/GmailMailbox";
import { api, type DashboardApp } from "@/lib/api";
import { useApi } from "@/lib/useApi";

function Block({ title, href, children }: { title: string; href?: string; children: React.ReactNode }) {
  return (
    <section className="min-w-0 rounded-xl border border-line bg-surface-2 p-3">
      <div className="mb-1.5 flex items-center justify-between"><h3 className="text-xs font-bold uppercase tracking-wide text-muted">{title}</h3>{href && <Link href={href} className="text-xs font-semibold text-accent hover:underline">Open</Link>}</div>
      {children}
    </section>
  );
}
const none = <p className="text-xs text-muted">Nothing to show.</p>;

/** The Microsoft card on the Daksha dashboard: unread Outlook mail, the next events, recent OneDrive files and the work items assigned to you. */
export function MicrosoftDashboardCard({ app, onChanged }: { app: DashboardApp; onChanged?: () => void }) {
  const [manageOpen, setManageOpen] = useState(false);
  const { state, reload } = useApi(() => api.microsoftDashboard());

  return (
    <div className={`min-h-full p-4 ${tileClass}`}>
      <header className="flex items-start gap-3">
        <BrandTile icon={app.icon} color={app.color} size={40} />
        <div className="min-w-0 flex-1">
          <h2 className="text-xl font-bold leading-tight">Microsoft</h2>
          <p className="truncate text-xs text-muted sm:text-sm">{app.accountIdentifier ?? "Outlook, Calendar, OneDrive and Azure DevOps"}</p>
        </div>
        <button type="button" onClick={() => setManageOpen(true)} aria-label="Manage Microsoft" className="rounded-xl border border-line p-2 text-muted hover:bg-surface-2">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" className="h-4.5 w-4.5"><path d="M12 15a3 3 0 100-6 3 3 0 000 6zM19 12l2-1-2-4-2 .5-1.5-1L15 4h-4l-.5 2.5-1.5 1L7 7l-2 4 2 1v2l-2 1 2 4 2-.5 1.5 1L11 20h4l.5-2.5 1.5-1 2 .5 2-4-2-1z" /></svg>
        </button>
      </header>

      <div className="mt-3">
        {state.status === "loading" && <Loading label="Loading Microsoft" />}
        {state.status === "error" && <ErrorState error={state.error} onRetry={reload} />}
        {state.status === "ready" && (
          <div className="space-y-3">
            {state.data.errors.map((e) => <p key={e} role="alert" className="text-xs text-danger">{e}</p>)}
            <div className="grid gap-3 md:grid-cols-2">
              {state.data.features.includes("mail") && (
                <Block title={`Outlook${state.data.unread ? ` · ${state.data.unread} unread` : ""}`} href="/apps/microsoft?tab=mail">
                  {state.data.messages.length === 0 ? none : <ul className="space-y-1.5">{state.data.messages.map((m) => (
                    <li key={m.id} className="min-w-0 text-sm"><span className={`block truncate ${m.isRead ? "" : "font-bold"}`}>{m.from}</span><span className="block truncate text-xs text-muted">{m.subject} · {listDate(m.received)}</span></li>))}</ul>}
                </Block>
              )}
              {state.data.features.includes("calendar") && (
                <Block title="Next 7 days" href="/calendar">
                  {state.data.events.length === 0 ? none : <ul className="space-y-1.5">{state.data.events.map((e) => (
                    <li key={e.id} className="min-w-0 text-sm"><span className="block truncate font-semibold">{e.title}</span><span className="block truncate text-xs text-muted">{e.allDay ? e.start : new Date(e.start).toLocaleString([], { weekday: "short", hour: "numeric", minute: "2-digit" })}</span></li>))}</ul>}
                </Block>
              )}
              {state.data.features.includes("onedrive") && (
                <Block title="Recent files" href="/apps/microsoft?tab=onedrive">
                  {state.data.files.length === 0 ? none : <ul className="space-y-1">{state.data.files.map((f) => (
                    <li key={f.id} className="truncate text-sm"><a href={f.webUrl ?? "#"} target="_blank" rel="noreferrer" className="hover:underline">{f.name}</a></li>))}</ul>}
                </Block>
              )}
              {state.data.features.includes("devops") && (
                <Block title="My work items" href="/apps/microsoft?tab=devops">
                  {state.data.workItems.length === 0 ? none : <ul className="space-y-1.5">{state.data.workItems.map((w) => (
                    <li key={w.id} className="min-w-0 text-sm"><a href={w.url} target="_blank" rel="noreferrer" className="block truncate hover:underline">#{w.id} {w.title}</a><span className="block text-xs text-muted">{w.state} · {w.project}</span></li>))}</ul>}
                </Block>
              )}
            </div>
          </div>
        )}
      </div>

      <Modal wide fixedHeight open={manageOpen} onClose={() => { setManageOpen(false); reload(); }}>
        <MicrosoftManagePanel onChanged={() => { reload(); onChanged?.(); }} />
      </Modal>
    </div>
  );
}
