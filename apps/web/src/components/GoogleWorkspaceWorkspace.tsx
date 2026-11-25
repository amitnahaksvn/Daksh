"use client";
import { useEffect, useState } from "react";
import { CreatePanel, DriveBrowser, FileModal, GOOGLE_SERVICES, GoogleConnectCard, googleCallbackError } from "@/components/GoogleWorkspaceParts";
import { GoogleWorkspaceManagePanel } from "@/components/GoogleWorkspaceManagePanel";
import { Modal } from "@/components/Modal";
import { EmptyState, ErrorState, Loading, secondaryButtonClass } from "@/components/ui";
import { api, type GoogleFile, type GoogleService, type GoogleWorkspaceSettings } from "@/lib/api";
import { useApi } from "@/lib/useApi";

const TAB_KIND: Record<GoogleService, string> = { drive: "all", docs: "docs", sheets: "sheets", slides: "slides" };
const TAB_LABEL: Record<GoogleService, string> = { drive: "Drive", docs: "Docs", sheets: "Sheets", slides: "Slides" };

/** The /apps/google-drive page: connect once, then Drive, Docs, Sheets and Slides in one place. */
export function GoogleWorkspaceWorkspace() {
  const { state, reload } = useApi(() => api.googleStatus());
  const [manage, setManage] = useState(false);
  const [settings, setSettings] = useState<GoogleWorkspaceSettings | null>(null);
  const [open, setOpen] = useState<GoogleFile | null>(null);
  const [created, setCreated] = useState<string | null>(null);
  const connected = state.status === "ready" && state.data.connected;
  useEffect(() => { if (connected && !manage) api.googleSettings().then(setSettings, () => setSettings(null)); }, [connected, manage]);
  const params = typeof window === "undefined" ? null : new URLSearchParams(window.location.search);
  const [tab, setTab] = useState<GoogleService | null>(() => (params?.get("tab") as GoogleService | null) ?? null);

  if (state.status === "loading") return <Loading label="Loading Google" />;
  if (state.status === "error") return <ErrorState error={state.error} onRetry={reload} />;
  const status = state.data;
  if (!status.connected) return <GoogleConnectCard onConnected={reload} />;

  const tabs = GOOGLE_SERVICES.filter((s) => status.services.find((x) => x.id === s.id)?.enabled);
  const active = tabs.find((t) => t.id === tab) ?? tabs[0];
  const enabled = { doc: status.services.some((s) => s.id === "docs" && s.enabled), sheets: status.services.some((s) => s.id === "sheets" && s.enabled), slides: status.services.some((s) => s.id === "slides" && s.enabled) };
  const openIn = settings?.openIn ?? "daksha";

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-2">
        <div role="tablist" aria-label="Google services" className="flex flex-wrap gap-1">
          {tabs.map((t) => (
            <button key={t.id} role="tab" aria-selected={active?.id === t.id} onClick={() => setTab(t.id)}
              className={`rounded-full px-4 py-2 text-sm font-semibold ${active?.id === t.id ? "bg-accent text-accent-contrast" : "border border-line text-muted hover:bg-surface-2"}`}>{TAB_LABEL[t.id]}</button>
          ))}
        </div>
        <span className="ml-auto truncate text-xs text-muted">{status.email}</span>
        <button type="button" onClick={() => setManage(true)} className={secondaryButtonClass}>Manage</button>
      </div>

      {created !== null && (
        <p role="status" className="rounded-[1.25rem] bg-lime p-3 text-sm text-on-tint">
          Created. {created && <a href={created} target="_blank" rel="noreferrer" className="font-semibold underline">Open it in Google</a>}
        </p>
      )}

      {!active ? <EmptyState>No Google service is turned on. Open Manage to choose one.</EmptyState> : (
        <>
          {status.write && (active.id === "docs" || active.id === "sheets") && <CreatePanel key={active.id} kind={active.id === "docs" ? "doc" : "sheet"} onCreated={setCreated} />}
          <DriveBrowser key={active.id} initialKind={TAB_KIND[active.id]} initialView={active.id === "drive" ? "recent" : "mine"} openIn={openIn} enabled={enabled} onOpen={setOpen} />
        </>
      )}

      <p className="text-xs text-muted">{GOOGLE_SERVICES.length} Google services share this one sign-in, together with Gmail and Calendar.</p>
      <FileModal file={open} canWrite={status.write} onClose={() => setOpen(null)} />
      <Modal wide fixedHeight open={manage} onClose={() => { setManage(false); reload(); }}>
        <GoogleWorkspaceManagePanel onChanged={reload} />
      </Modal>
    </div>
  );
}

export { googleCallbackError };
