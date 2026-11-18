"use client";
import { useEffect, useState } from "react";
import { BrandTile, StatusChip } from "@/components/integrations";
import { GmailConnectCard } from "@/components/GmailConnectCard";
import { CONTROL_TABS, GmailControlCentre, type ControlTabId } from "@/components/GmailControlCentre";
import { Card, Row, Toggle } from "@/components/WhatsAppSettingsParts";
import { buttonClass, Dropdown, ErrorState, inputClass, Loading } from "@/components/ui";
import { api, ApiError, type GmailSettings } from "@/lib/api";
import { useApi } from "@/lib/useApi";

type BasicTabId = "general" | "reading" | "compose" | "dashboard";
type TabId = BasicTabId | ControlTabId;
const BASIC_TABS: { id: BasicTabId; label: string }[] = [
  { id: "general", label: "General" },
  { id: "reading", label: "Reading" },
  { id: "compose", label: "Compose" },
  { id: "dashboard", label: "Dashboard" },
];
const TABS: { id: TabId; label: string }[] = [...BASIC_TABS, ...CONTROL_TABS];
const isControlTab = (t: TabId): t is ControlTabId => CONTROL_TABS.some((c) => c.id === t);

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
export function GmailManagePanel({ onChanged, initialTab }: { onChanged?: () => void; initialTab?: ControlTabId }) {
  const { state, reload } = useApi(() => api.appDetail("gmail"));
  const [tab, setTab] = useState<TabId>(initialTab ?? "general");
  const [controlDirty, setControlDirty] = useState(false);

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
      </header>

      {!connected ? (
        <div className="mt-5">
          <GmailConnectCard onConnected={() => { reload(); onChanged?.(); }} />
        </div>
      ) : (
        <>
          <div role="tablist" aria-label="Gmail settings" className="sticky top-0 z-10 -mx-1 mt-5 flex gap-1 overflow-x-auto border-b border-line bg-surface px-1">
            {TABS.map((t) => (
              <button
                key={t.id}
                role="tab"
                aria-selected={tab === t.id}
                onClick={() => {
                  // The control-centre tabs share one unsaved draft; keep it safe when leaving them for the simple tabs.
                  if (controlDirty && isControlTab(tab) && !isControlTab(t.id) && !window.confirm("You have unsaved Gmail settings. Leave without saving?")) return;
                  setTab(t.id);
                }}
                className={`shrink-0 border-b-2 px-4 py-2 text-sm font-semibold transition-colors ${tab === t.id ? "border-accent text-text" : "border-transparent text-muted hover:text-text"}`}
              >
                {t.label}
              </button>
            ))}
          </div>
          <div className="mt-4">
            {!isControlTab(tab) && <GmailSettingsTabs tab={tab} connectionId={connection!.connectionId} onChanged={onChanged} />}
            {/* Stays mounted (hidden) while a simple tab is open so unsaved edits aren't lost. */}
            <div hidden={!isControlTab(tab)}>
              <GmailControlCentre tab={isControlTab(tab) ? tab : undefined} hideTabs onDirtyChange={setControlDirty} onAccountsChanged={() => { reload(); onChanged?.(); }} />
            </div>
          </div>
        </>
      )}
    </div>
  );
}

function GmailSettingsTabs({ tab, connectionId, onChanged }: {
  tab: BasicTabId;
  connectionId: string;
  onChanged?: () => void;
}) {
  const [settings, setSettings] = useState<GmailSettings | null>(null);
  const [signature, setSignature] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);

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

  if (!settings && !error) return <Loading label="Loading settings" />;

  return (
    <div className="space-y-3">
      {error && <p role="alert" className="rounded-[1.25rem] bg-danger-bg p-3 text-sm text-danger">{error}</p>}
      {saved && <p role="status" className="rounded-[1.25rem] bg-lime p-3 text-sm text-on-tint">{saved}</p>}

      {settings && tab === "general" && (
        <Card>
          <Row label="Show Gmail dashboard" hint="Show the Gmail dashboard (briefing, statistics, priority emails) on your Daksha dashboard. Turn off to hide it." search="">
            <Toggle checked={settings.showOnDashboard} onChange={(v) => void toggleDashboard(v)} />
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

    </div>
  );
}
