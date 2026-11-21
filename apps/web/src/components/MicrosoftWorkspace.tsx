"use client";
import Link from "next/link";
import { useEffect, useState } from "react";
import { DevOpsPanel, MICROSOFT_SERVICES, MicrosoftConnectCard, OneDriveBrowser, OutlookMailbox, microsoftCallbackError } from "@/components/MicrosoftParts";
import { MicrosoftManagePanel } from "@/components/MicrosoftManagePanel";
import { Modal } from "@/components/Modal";
import { EmptyState, ErrorState, Loading, secondaryButtonClass } from "@/components/ui";
import { api, type MicrosoftFeature, type MicrosoftSettings } from "@/lib/api";
import { useApi } from "@/lib/useApi";

const TABS: { id: MicrosoftFeature; label: string }[] = [
  { id: "mail", label: "Mail" }, { id: "calendar", label: "Calendar" }, { id: "onedrive", label: "OneDrive" }, { id: "devops", label: "Azure DevOps" },
];

/** The /apps/microsoft page: connect once, then Outlook mail, Calendar, OneDrive and Azure DevOps in one place. */
export function MicrosoftWorkspace() {
  const { state, reload } = useApi(() => api.microsoftStatus());
  const [manage, setManage] = useState(false);
  const [settings, setSettings] = useState<MicrosoftSettings | null>(null);
  const connected = state.status === "ready" && state.data.connected;
  useEffect(() => { if (connected && !manage) api.microsoftSettings().then(setSettings, () => setSettings(null)); }, [connected, manage]);
  const params = typeof window === "undefined" ? null : new URLSearchParams(window.location.search);
  const [tab, setTab] = useState<MicrosoftFeature | null>(() => (params?.get("tab") as MicrosoftFeature | null) ?? null);
  const feedbackError = params?.get("error") ?? null;

  // In the sign-in popup: Microsoft sent the browser here. Tell the opener the result, then close.
  const feedback = params?.get("microsoft") ?? params?.get("error") ?? null;
  const inPopup = typeof window !== "undefined" && Boolean(window.opener) && window.opener !== window;
  useEffect(() => {
    if (!feedback || !inPopup) return;
    try { window.opener.postMessage({ type: "daksh-microsoft-oauth", result: feedback }, window.location.origin); } catch { return; }
    window.close();
  }, [feedback, inPopup]);
  if (inPopup && feedback) return <Loading label="Finishing Microsoft sign-in" />;

  if (state.status === "loading") return <Loading label="Loading Microsoft" />;
  if (state.status === "error") return <ErrorState error={state.error} onRetry={reload} />;
  const status = state.data;
  if (!status.connected) return <MicrosoftConnectCard onConnected={reload} initialError={feedbackError ? microsoftCallbackError(feedbackError) : null} />;

  const tabs = TABS.filter((t) => status.features.includes(t.id));
  const active = tabs.find((t) => t.id === tab) ?? tabs[0];

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-2">
        <div role="tablist" aria-label="Microsoft services" className="flex flex-wrap gap-1">
          {tabs.map((t) => (
            <button key={t.id} role="tab" aria-selected={active?.id === t.id} onClick={() => setTab(t.id)}
              className={`rounded-full px-4 py-2 text-sm font-semibold ${active?.id === t.id ? "bg-accent text-accent-contrast" : "border border-line text-muted hover:bg-surface-2"}`}>{t.label}</button>
          ))}
        </div>
        <span className="ml-auto truncate text-xs text-muted">{status.email}</span>
        <button type="button" onClick={() => setManage(true)} className={secondaryButtonClass}>Manage</button>
      </div>

      {!active ? <EmptyState>No Microsoft service is turned on. Open Manage to choose one.</EmptyState>
        : active.id === "mail" ? <OutlookMailbox canWrite={status.write} showImages={settings?.showImages ?? false} />
        : active.id === "calendar" ? <EmptyState>Your Outlook events appear in <Link href="/calendar" className="font-semibold underline">Calendar</Link>, together with Google.</EmptyState>
        : active.id === "onedrive" ? <OneDriveBrowser />
        : <DevOpsPanel />}

      <p className="text-xs text-muted">{MICROSOFT_SERVICES.length} Microsoft services share this one sign-in.</p>
      <Modal wide fixedHeight open={manage} onClose={() => { setManage(false); reload(); }}>
        <MicrosoftManagePanel onChanged={reload} />
      </Modal>
    </div>
  );
}
