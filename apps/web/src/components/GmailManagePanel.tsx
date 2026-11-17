"use client";
import Link from "next/link";
import { useEffect, useState } from "react";
import { BrandTile, StatusChip } from "@/components/integrations";
import { GmailConnectCard } from "@/components/GmailConnectCard";
import { Card, Row, Toggle } from "@/components/WhatsAppSettingsParts";
import { buttonClass, Dropdown, ErrorState, inputClass, Loading, secondaryButtonClass } from "@/components/ui";
import { api, ApiError, type GmailSettings } from "@/lib/api";
import { useApi } from "@/lib/useApi";

type TabId = "general" | "reading" | "compose" | "dashboard" | "account";
const TABS: { id: TabId; label: string }[] = [
  { id: "general", label: "General" },
  { id: "reading", label: "Reading" },
  { id: "compose", label: "Compose" },
  { id: "dashboard", label: "Dashboard" },
  { id: "account", label: "Account" },
];

const DASHBOARD_FILTERS = [
  { value: "in:inbox", label: "Inbox" },
  { value: "in:inbox is:unread", label: "Unread in inbox" },
  { value: "is:starred", label: "Starred" },
  { value: "in:inbox category:primary", label: "Primary" },
];
const COUNT_OPTIONS = [3, 4, 5, 6, 8, 10].map((n) => ({ value: String(n), label: `${n} emails` }));

function errorText(err: unknown, fallback: string) {
  return err instanceof ApiError ? err.message : fallback;
}

/** The Gmail "Manage" panel (opened from the Apps page): connect Gmail, or tune how it behaves. */
export function GmailManagePanel({ onChanged }: { onChanged?: () => void }) {
  const { state, reload } = useApi(() => api.appDetail("gmail"));
  const [tab, setTab] = useState<TabId>("general");

  if (state.status === "loading") return <Loading label="Loading Gmail" />;
  if (state.status === "error") return <ErrorState error={state.error} onRetry={reload} />;

  const { catalog, connection } = state.data;
  const connected = connection?.status === "Connected";

  return (
    <div>
      <header className="flex flex-wrap items-center gap-3 pr-10">
        <BrandTile icon={catalog.icon} color={catalog.color} size={44} />
        <div className="min-w-0">
          <h2 className="text-xl font-bold">Gmail</h2>
          <div className="mt-0.5 flex items-center gap-2">
            <StatusChip status={connection?.status ?? "NotConnected"} />
            {connection?.accountIdentifier && <span className="truncate text-xs text-muted">{connection.accountIdentifier}</span>}
          </div>
        </div>
        {connected && <Link href="/apps/gmail" className={`${buttonClass} ml-auto px-4 py-2 text-xs`}>Open Gmail</Link>}
      </header>

      {!connected ? (
        <div className="mt-5">
          <GmailConnectCard onConnected={() => { reload(); onChanged?.(); }} />
        </div>
      ) : (
        <>
          <div role="tablist" aria-label="Gmail settings" className="mt-5 flex gap-1 overflow-x-auto border-b border-line">
            {TABS.map((t) => (
              <button
                key={t.id}
                role="tab"
                aria-selected={tab === t.id}
                onClick={() => setTab(t.id)}
                className={`shrink-0 border-b-2 px-4 py-2 text-sm font-semibold transition-colors ${tab === t.id ? "border-accent text-text" : "border-transparent text-muted hover:text-text"}`}
              >
                {t.label}
              </button>
            ))}
          </div>
          <div className="mt-4">
            <GmailSettingsTabs
              tab={tab}
              connectionId={connection!.connectionId}
              account={connection!.accountIdentifier}
              onDisconnected={() => { reload(); onChanged?.(); }}
              onChanged={onChanged}
            />
          </div>
        </>
      )}
    </div>
  );
}

function GmailSettingsTabs({ tab, connectionId, account, onDisconnected, onChanged }: {
  tab: TabId;
  connectionId: string;
  account: string | null;
  onDisconnected: () => void;
  onChanged?: () => void;
}) {
  const [settings, setSettings] = useState<GmailSettings | null>(null);
  const [signature, setSignature] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    api.gmailSettings().then(
      (s) => { setSettings(s); setSignature(s.signature); },
      (err) => setError(errorText(err, "Could not load Gmail settings.")),
    );
  }, []);

  function flash(message: string) {
    setSaved(message);
    window.setTimeout(() => setSaved((current) => (current === message ? null : current)), 2500);
  }

  async function update(change: Parameters<typeof api.updateGmailSettings>[0], message = "Saved") {
    setError(null);
    try {
      const next = await api.updateGmailSettings(change);
      setSettings(next);
      flash(message);
    } catch (err) {
      setError(errorText(err, "Could not save your Gmail settings."));
    }
  }

  async function toggleDashboard(show: boolean) {
    setError(null);
    try {
      if (show) {
        await api.addToDashboard(connectionId);
      } else {
        const entry = (await api.dashboardApps()).find((a) => a.connectionId === connectionId);
        if (entry) await api.removeFromDashboard(entry.id);
      }
      setSettings(await api.gmailSettings());
      onChanged?.();
      flash(show ? "Gmail added to your dashboard" : "Gmail removed from your dashboard");
    } catch (err) {
      setError(errorText(err, "Could not update the dashboard."));
    }
  }

  async function disconnect() {
    if (!window.confirm("Disconnect Gmail and revoke Daksh's Google access?")) return;
    setBusy(true);
    setError(null);
    try {
      await api.disconnectGmail();
      onDisconnected();
    } catch (err) {
      setError(errorText(err, "Could not disconnect Gmail."));
      setBusy(false);
    }
  }

  if (!settings && !error) return <Loading label="Loading settings" />;

  return (
    <div className="space-y-3">
      {error && <p role="alert" className="rounded-[1.25rem] bg-danger-bg p-3 text-sm text-danger">{error}</p>}
      {saved && <p role="status" className="rounded-[1.25rem] bg-lime p-3 text-sm text-on-tint">{saved}</p>}

      {settings && tab === "general" && (
        <Card>
          <Row label="Show Gmail on dashboard" hint="Pin your latest emails to the Daksha dashboard." search="">
            <Toggle checked={settings.showOnDashboard} onChange={(v) => void toggleDashboard(v)} />
          </Row>
          <Row label="Open full mailbox" hint="Read, search and send mail in the Gmail view." search="">
            <Link href="/apps/gmail" className={`${secondaryButtonClass} px-4 py-2 text-xs`}>Open Gmail</Link>
          </Row>
        </Card>
      )}

      {settings && tab === "reading" && (
        <Card>
          <Row label="Show images in emails" hint="Off by default: photos and remote images in a message stay hidden until you choose to display them (also stops tracking pixels)." search="">
            <Toggle checked={settings.showImages} onChange={(v) => void update({ showImages: v })} />
          </Row>
        </Card>
      )}

      {settings && tab === "compose" && (
        <div className="rounded-[1.25rem] border border-line p-4">
          <label htmlFor="gmail-signature" className="text-sm font-semibold">Signature</label>
          <p className="mt-0.5 text-xs text-muted">Added to the end of every email you send from Daksha.</p>
          <textarea
            id="gmail-signature"
            value={signature}
            onChange={(e) => setSignature(e.target.value)}
            rows={5}
            maxLength={2000}
            className={`${inputClass} mt-2 resize-y`}
            placeholder={"Best regards,\nYour name"}
          />
          <button onClick={() => void update({ signature }, "Signature saved")} disabled={signature === settings.signature} className={`${buttonClass} mt-3`}>
            Save signature
          </button>
        </div>
      )}

      {settings && tab === "dashboard" && (
        <Card>
          <Row label="Emails to show" hint="How many recent emails the dashboard card lists." search="">
            <Dropdown
              className="w-36"
              value={String(settings.dashboardCount)}
              options={COUNT_OPTIONS}
              onChange={(v) => void update({ dashboardCount: Number(v) })}
            />
          </Row>
          <Row label="Which emails" hint="The mail the dashboard card previews." search="">
            <Dropdown
              className="w-52"
              value={settings.dashboardQuery}
              options={DASHBOARD_FILTERS}
              onChange={(v) => void update({ dashboardQuery: v })}
            />
          </Row>
        </Card>
      )}

      {tab === "account" && (
        <div className="rounded-[1.25rem] border border-line p-4">
          <p className="text-sm font-semibold">{account}</p>
          <p className="mt-0.5 text-xs text-muted">Daksha can read and send mail for this account. Disconnecting revokes that access and removes the stored Google tokens.</p>
          <button onClick={disconnect} disabled={busy} className={`${secondaryButtonClass} mt-3`}>Disconnect Gmail</button>
        </div>
      )}
    </div>
  );
}
