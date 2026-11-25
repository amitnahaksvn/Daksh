"use client";
import Link from "next/link";
import { useState } from "react";
import { BrandTile } from "@/components/integrations";
import { GoogleWorkspaceManagePanel } from "@/components/GoogleWorkspaceManagePanel";
import { Modal } from "@/components/Modal";
import { ErrorState, Loading, tileClass } from "@/components/ui";
import { api, type DashboardApp } from "@/lib/api";
import { useApi } from "@/lib/useApi";

const ICON = { folder: "📁", doc: "📝", sheet: "📊", slides: "📽️", pdf: "📕", image: "🖼️", file: "📄" } as const;

/** The Google Drive card on the Daksha dashboard: the files you chose to see (recent, shared, starred or in My Drive). */
export function GoogleWorkspaceDashboardCard({ app, onChanged }: { app: DashboardApp; onChanged?: () => void }) {
  const [manageOpen, setManageOpen] = useState(false);
  const { state, reload } = useApi(() => api.googleWorkspaceDashboard());

  return (
    <div className={`min-h-full p-4 ${tileClass}`}>
      <header className="flex items-start gap-3">
        <BrandTile icon={app.icon} color={app.color} size={40} />
        <div className="min-w-0 flex-1">
          <h2 className="text-xl font-bold leading-tight">Google Drive</h2>
          <p className="truncate text-xs text-muted sm:text-sm">{app.accountIdentifier ?? "Drive, Docs, Sheets and Slides"}</p>
        </div>
        <Link href="/apps/google-drive" className="self-center text-xs font-semibold text-accent hover:underline">Open</Link>
        <button type="button" onClick={() => setManageOpen(true)} aria-label="Manage Google Drive" className="rounded-xl border border-line p-2 text-muted hover:bg-surface-2">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" className="h-4.5 w-4.5"><path d="M12 15a3 3 0 100-6 3 3 0 000 6zM19 12l2-1-2-4-2 .5-1.5-1L15 4h-4l-.5 2.5-1.5 1L7 7l-2 4 2 1v2l-2 1 2 4 2-.5 1.5 1L11 20h4l.5-2.5 1.5-1 2 .5 2-4-2-1z" /></svg>
        </button>
      </header>

      <div className="mt-3">
        {state.status === "loading" && <Loading label="Loading Google Drive" />}
        {state.status === "error" && <ErrorState error={state.error} onRetry={reload} />}
        {state.status === "ready" && (
          <div className="space-y-2">
            {state.data.errors.map((e) => <p key={e} role="alert" className="text-xs text-danger">{e}</p>)}
            {!state.data.services.includes("drive")
              ? <p className="text-xs text-muted">Google Drive is turned off. Docs, Sheets and Slides are still available from the Drive page.</p>
              : state.data.files.length === 0 ? <p className="text-xs text-muted">Nothing to show.</p>
              : <ul className="space-y-1.5">{state.data.files.map((f) => (
                <li key={f.id} className="flex min-w-0 items-center gap-2 text-sm">
                  <span aria-hidden>{ICON[f.kind]}</span>
                  <a href={f.webUrl ?? "#"} target="_blank" rel="noreferrer" className="min-w-0 flex-1 truncate hover:underline">{f.name}</a>
                  <span className="shrink-0 text-xs text-muted">{f.modified ? new Date(f.modified).toLocaleDateString() : ""}</span>
                </li>))}</ul>}
          </div>
        )}
      </div>

      <Modal wide fixedHeight open={manageOpen} onClose={() => { setManageOpen(false); reload(); }}>
        <GoogleWorkspaceManagePanel onChanged={() => { reload(); onChanged?.(); }} />
      </Modal>
    </div>
  );
}
