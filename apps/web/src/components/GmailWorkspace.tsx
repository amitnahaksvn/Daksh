"use client";
import { useEffect, useState, useSyncExternalStore } from "react";
import { api, type GmailSettings } from "@/lib/api";
import { useApi } from "@/lib/useApi";
import { GmailConnectCard, callbackError } from "@/components/GmailConnectCard";
import { GmailMailbox } from "@/components/GmailMailbox";
import { GmailManagePanel } from "@/components/GmailManagePanel";
import { Modal } from "@/components/Modal";
import { ErrorState, Loading } from "@/components/ui";

function callbackSnapshot() {
  if (typeof window === "undefined") return "";
  const params = new URLSearchParams(window.location.search);
  if (params.get("status") === "connected") return "connected";
  return params.get("error") ?? "";
}

function subscribeToLocation(callback: () => void) {
  window.addEventListener("popstate", callback);
  return () => window.removeEventListener("popstate", callback);
}

/** The /apps/gmail page: connect Gmail, then use a Gmail-style mailbox. Also the landing page of the OAuth popup. */
export function GmailWorkspace() {
  const { state: detail, reload } = useApi(() => api.appDetail("gmail"));
  const feedback = useSyncExternalStore(subscribeToLocation, callbackSnapshot, () => "");
  const connection = detail.status === "ready" ? detail.data.connection : null;
  const connected = connection?.status === "Connected";
  const [settings, setSettings] = useState<GmailSettings | null>(null);
  const [manageOpen, setManageOpen] = useState(false);
  // The dashboard card links here with ?compose=1, ?q=<search>, ?open=<message id> or ?focus=search.
  const [initial] = useState(() => {
    if (typeof window === "undefined") return undefined;
    const p = new URLSearchParams(window.location.search);
    const start = { query: p.get("q") ?? undefined, compose: p.get("compose") === "1", focusSearch: p.get("focus") === "search", openMessageId: p.get("open") ?? undefined };
    return start.query || start.compose || start.focusSearch || start.openMessageId ? start : undefined;
  });

  // In the OAuth popup: Google redirected here. Tell the opener the result, then close.
  const inPopup = typeof window !== "undefined" && Boolean(window.opener) && window.opener !== window;
  useEffect(() => {
    if (!feedback || !inPopup) return;
    try {
      window.opener.postMessage({ type: "daksh-gmail-oauth", result: feedback }, window.location.origin);
    } catch { return; }
    window.close();
  }, [feedback, inPopup]);

  useEffect(() => {
    if (!connected) return;
    api.gmailSettings().then(setSettings, () => setSettings(null));
  }, [connected, manageOpen]);

  if (inPopup && feedback) return <div className="p-6"><Loading label="Finishing Gmail sign-in" /></div>;

  return (
    <div className="space-y-4">
      {detail.status === "loading" && <Loading label="Loading Gmail connection" />}
      {detail.status === "error" && <ErrorState error={detail.error} onRetry={reload} />}

      {detail.status === "ready" && !connected && (
        <GmailConnectCard
          onConnected={reload}
          initialError={feedback && feedback !== "connected" ? callbackError(feedback) : null}
        />
      )}

      {connected && (
        <>
          <GmailMailbox showImages={settings?.showImages ?? false} onOpenSettings={() => setManageOpen(true)} initial={initial} />
          <Modal wide fixedHeight open={manageOpen} onClose={() => { setManageOpen(false); reload(); }}>
            <GmailManagePanel onChanged={reload} />
          </Modal>
        </>
      )}
    </div>
  );
}
