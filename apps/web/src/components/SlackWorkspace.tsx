"use client";
import { useEffect, useState } from "react";
import { SlackConnectCard, SlackInbox, slackCallbackError } from "@/components/SlackParts";
import { SlackManagePanel } from "@/components/SlackManagePanel";
import { Modal } from "@/components/Modal";
import { ErrorState, Loading, secondaryButtonClass } from "@/components/ui";
import { api } from "@/lib/api";
import { useApi } from "@/lib/useApi";

/** The /apps/slack page: connect once, then read channels and direct messages, search and reply. */
export function SlackWorkspace() {
  const { state, reload } = useApi(() => api.slackStatus());
  const [manage, setManage] = useState(false);
  const params = typeof window === "undefined" ? null : new URLSearchParams(window.location.search);
  const feedbackError = params?.get("error") ?? null;
  const initialWorkspace = params?.get("workspace") ?? null;

  // In the sign-in popup: Slack sent the browser here. Tell the opener the result, then close.
  const feedback = params?.get("slack") ?? params?.get("error") ?? null;
  const inPopup = typeof window !== "undefined" && Boolean(window.opener) && window.opener !== window;
  useEffect(() => {
    if (!feedback || !inPopup) return;
    try { window.opener.postMessage({ type: "daksh-slack-oauth", result: feedback }, window.location.origin); } catch { return; }
    window.close();
  }, [feedback, inPopup]);
  if (inPopup && feedback) return <Loading label="Finishing Slack sign-in" />;

  if (state.status === "loading") return <Loading label="Loading Slack" />;
  if (state.status === "error") return <ErrorState error={state.error} onRetry={reload} />;
  const status = state.data;
  if (!status.connected) return <SlackConnectCard onConnected={reload} initialError={feedbackError ? slackCallbackError(feedbackError) : null} />;

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-2">
        <span className="min-w-0 flex-1 truncate text-sm font-semibold">{status.workspaces.map((w) => w.teamName).join(" · ")}</span>
        <button type="button" onClick={() => setManage(true)} className={secondaryButtonClass}>Manage</button>
      </div>
      <SlackInbox workspaces={status.workspaces} initialConnectionId={initialWorkspace} />
      <Modal wide fixedHeight open={manage} onClose={() => { setManage(false); reload(); }}>
        <SlackManagePanel onChanged={reload} />
      </Modal>
    </div>
  );
}
