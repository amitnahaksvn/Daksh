"use client";
import { useEffect, useState } from "react";
import { BrandTile } from "@/components/integrations";
import { SlackConnectCard, signInWithSlack, slackResultMessage } from "@/components/SlackParts";
import { Card, Row, Toggle } from "@/components/WhatsAppSettingsParts";
import { buttonClass, Dropdown, ErrorState, Loading } from "@/components/ui";
import { api, ApiError, type SlackSettings, type SlackSettingsUpdate } from "@/lib/api";
import { useApi } from "@/lib/useApi";

type TabId = "general" | "messages" | "notifications" | "dashboard";
const TABS: { id: TabId; label: string }[] = [{ id: "general", label: "General" }, { id: "messages", label: "Messages" }, { id: "notifications", label: "Notifications" }, { id: "dashboard", label: "Dashboard" }];
const COUNTS = [3, 4, 5, 6, 8, 10].map((n) => ({ value: String(n), label: `${n} items` }));
const SOURCES = [{ value: "all", label: "Direct messages and mentions" }, { value: "dms", label: "Direct messages only" }, { value: "mentions", label: "Mentions only" }];

const errorText = (err: unknown, fallback: string) => (err instanceof ApiError ? err.message : fallback);

function Section({ title, hint, children }: { title: string; hint?: string; children: React.ReactNode }) {
  return <section><h3 className="text-base font-bold">{title}</h3>{hint && <p className="mb-2 text-xs text-muted">{hint}</p>}{!hint && <div className="mb-2" />}<Card>{children}</Card></section>;
}

/** The Slack "Manage" panel, laid out like Gmail's and Microsoft's: connect, or tune how Slack behaves. */
export function SlackManagePanel({ onChanged }: { onChanged?: () => void }) {
  const { state, reload } = useApi(async () => {
    const [status, available] = await Promise.all([api.slackStatus(), api.availableDashboardApps()]);
    const ids = new Map(status.workspaces.map((w) => [w.connectionId, available.find((a) => a.connectionId === w.connectionId)?.dashboardId ?? null]));
    return { status, dashboardIds: ids };
  });
  const [tab, setTab] = useState<TabId>("general");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);
  const [settings, setSettings] = useState<SlackSettings | null>(null);
  const connected = state.status === "ready" && state.data.status.connected;

  useEffect(() => {
    if (!connected) return;
    api.slackSettings().then(setSettings, (err) => setError(errorText(err, "Could not load Slack settings.")));
  }, [connected]);

  if (state.status === "loading") return <Loading label="Loading Slack" />;
  if (state.status === "error") return <ErrorState error={state.error} onRetry={reload} />;
  const { status, dashboardIds } = state.data;
  const anyWrite = status.workspaces.some((w) => w.write);
  const allWrite = status.workspaces.length > 0 && status.workspaces.every((w) => w.write);

  function flash(message: string) { setSaved(message); window.setTimeout(() => setSaved((c) => (c === message ? null : c)), 2500); }

  async function run(action: () => Promise<unknown>, fallback: string, message?: string) {
    setBusy(true); setError(null);
    try { await action(); if (message) flash(message); onChanged?.(); reload(); }
    catch (err) { setError(errorText(err, fallback)); }
    finally { setBusy(false); }
  }

  async function update(change: SlackSettingsUpdate, message = "Saved") {
    setError(null);
    try { setSettings(await api.updateSlackSettings(change)); flash(message); onChanged?.(); }
    catch (err) { setError(errorText(err, "Could not save your Slack settings.")); }
  }

  const signIn = (write: boolean) => run(async () => { const m = slackResultMessage(await signInWithSlack(write)); if (m) throw new ApiError(0, m); }, "Could not start Slack sign-in.");
  const toggleWrite = (on: boolean) => (on ? signIn(true) : run(() => api.disableSlackWrite(), "Could not switch back to read-only."));

  return (
    <div>
      <header className="flex flex-wrap items-center gap-3 pr-10">
        <BrandTile icon="mono:#" color="#4A154B" size={44} />
        <div className="min-w-0">
          <h2 className="text-xl font-bold">Slack</h2>
          <p className="truncate text-xs text-muted">{status.connected ? status.workspaces.map((w) => w.teamName).join(", ") : "Not connected"}</p>
        </div>
      </header>

      {!status.configured && <p role="alert" className="mt-4 rounded-xl border border-line bg-surface-2 p-3 text-sm">Slack sign-in is not set up on the server yet. An administrator needs to add the Slack app (see docs/integrations/slack.md).</p>}

      {!status.connected ? (
        <div className="mt-5">
          <SlackConnectCard onConnected={() => { onChanged?.(); reload(); }} />
        </div>
      ) : (
        <>
          <div role="tablist" aria-label="Slack settings" className="sticky top-0 z-10 -mx-1 mt-5 flex gap-1 overflow-x-auto border-b border-line bg-surface px-1">
            {TABS.map((t) => (
              <button key={t.id} role="tab" aria-selected={tab === t.id} onClick={() => setTab(t.id)}
                className={`shrink-0 border-b-2 px-4 py-2 text-sm font-semibold transition-colors ${tab === t.id ? "border-accent text-text" : "border-transparent text-muted hover:text-text"}`}>{t.label}</button>
            ))}
          </div>

          <div className="mt-4 space-y-4">
            {error && <p role="alert" className="rounded-[1.25rem] bg-danger-bg p-3 text-sm text-danger">{error}</p>}
            {saved && <p role="status" className="rounded-[1.25rem] bg-lime p-3 text-sm text-on-tint">{saved}</p>}

            {tab === "general" && (
              <>
                <section>
                  <h3 className="text-base font-bold">Workspaces</h3>
                  <p className="mb-2 text-xs text-muted">Each Slack workspace you sign in to. Daksha acts as you in each, and sees only what you can see.</p>
                  <ul className="divide-y divide-line rounded-[1.25rem] border border-line">
                    {status.workspaces.map((w) => (
                      <li key={w.connectionId} className="flex flex-wrap items-center gap-3 px-4 py-3">
                        <div className="min-w-0 flex-1"><p className="truncate text-sm font-semibold">{w.teamName}</p><p className="text-xs text-muted">{w.write ? "Can read and post" : "Read-only"}{w.unread > 0 ? ` · ${w.unread} unread` : ""}</p></div>
                        <button type="button" disabled={busy} className="rounded-full border border-danger px-4 py-2 text-sm font-semibold text-danger hover:bg-danger-bg"
                          onClick={() => { if (window.confirm(`Disconnect ${w.teamName} from Daksh?`)) void run(() => api.disconnectSlack(w.connectionId), "Could not disconnect.", `${w.teamName} disconnected`); }}>Disconnect</button>
                      </li>
                    ))}
                  </ul>
                  <div className="mt-2"><button type="button" disabled={busy} onClick={() => void signIn(anyWrite)} className={`${buttonClass} px-4`}>Add another workspace</button></div>
                </section>
                {status.workspaces.length > 1 && settings && (
                  <Section title="Default workspace" hint="Used by search, the assistant and the dashboard when no workspace is chosen.">
                    <Row label="Default workspace" search="">
                      <Dropdown className="w-52" value={settings.defaultConnectionId ?? status.workspaces[0].connectionId} options={status.workspaces.map((w) => ({ value: w.connectionId, label: w.teamName }))} onChange={(v) => void update({ defaultConnectionId: v })} />
                    </Row>
                  </Section>
                )}
                <Section title="Access level" hint="Daksha starts read-only. Full access lets it post messages and mark conversations read. Slack asks you to approve this when you turn it on.">
                  <Row label="Full access" hint="Post and reply in Slack, and mark conversations read." search="">
                    <Toggle label="Full access" checked={allWrite} disabled={busy} onChange={(v) => void toggleWrite(v)} />
                  </Row>
                </Section>
                <Section title="Dashboard" hint="Show a Slack tile on your Daksha dashboard with your latest direct messages and mentions.">
                  {status.workspaces.map((w) => {
                    const id = dashboardIds.get(w.connectionId) ?? null;
                    return (
                      <Row key={w.connectionId} label={status.workspaces.length > 1 ? `Show ${w.teamName} on dashboard` : "Show Slack on dashboard"} search="">
                        <Toggle label={`Show ${w.teamName} on dashboard`} checked={id !== null} disabled={busy}
                          onChange={(v) => void run(() => (v ? api.addToDashboard(w.connectionId) : api.removeFromDashboard(id!)), "Could not change the dashboard.", v ? "Slack added to your dashboard" : "Slack removed from your dashboard")} />
                      </Row>
                    );
                  })}
                </Section>
                <section>
                  <h3 className="text-base font-bold">Open Slack</h3>
                  <div className="mt-2 flex flex-wrap gap-2 rounded-[1.25rem] border border-line p-3"><a href="/apps/slack" className={`${buttonClass} px-4`}>Open Slack in Daksh</a></div>
                </section>
              </>
            )}

            {tab === "messages" && (!settings ? <Loading label="Loading settings" /> : (
              <Section title="Reading" hint="How Slack messages behave when you read them in Daksh.">
                <Row label="Mark as read when I open a conversation" hint="Opening a channel or direct message in Daksh also marks it read in Slack. Needs full access." search="">
                  <Toggle label="Mark as read when I open a conversation" checked={settings.markReadOnOpen} onChange={(v) => void update({ markReadOnOpen: v })} />
                </Row>
                <Row label="Hide messages from apps and bots" hint="Leaves out automated posts such as alerts and integrations." search="">
                  <Toggle label="Hide messages from apps and bots" checked={settings.hideBots} onChange={(v) => void update({ hideBots: v })} />
                </Row>
              </Section>
            ))}

            {tab === "notifications" && (!settings ? <Loading label="Loading settings" /> : (
              <>
                {!status.eventsConfigured && <p className="rounded-xl border border-line bg-surface-2 p-3 text-sm">Live notifications need the Slack Events API, which is not set up on this server yet (see docs/integrations/slack.md). You can still read and search Slack.</p>}
                <Section title="Tell me about" hint="Direct messages and mentions that arrive while you are away show up on the dashboard and in the assistant.">
                  <Row label="Direct messages" search=""><Toggle label="Direct messages" checked={settings.notifyDms} onChange={(v) => void update({ notifyDms: v })} /></Row>
                  <Row label="Mentions" hint="When someone @mentions you in a channel." search=""><Toggle label="Mentions" checked={settings.notifyMentions} onChange={(v) => void update({ notifyMentions: v })} /></Row>
                </Section>
                <div><button type="button" disabled={busy} onClick={() => void run(() => api.markSlackActivityRead(), "Could not mark as read.", "All marked as read")} className={`${buttonClass} px-4`}>Mark everything as read</button></div>
              </>
            ))}

            {tab === "dashboard" && (!settings ? <Loading label="Loading settings" /> : (
              <Section title="Dashboard tile">
                <Row label="Items to show" hint="How many messages the dashboard tile lists." search="">
                  <Dropdown className="w-36" value={String(settings.dashboardCount)} options={COUNTS} onChange={(v) => void update({ dashboardCount: Number(v) })} />
                </Row>
                <Row label="What to show" hint="Which messages the dashboard tile previews." search="">
                  <Dropdown className="w-64" value={settings.dashboardSource} options={SOURCES} onChange={(v) => void update({ dashboardSource: v as SlackSettings["dashboardSource"] })} />
                </Row>
              </Section>
            ))}
          </div>
        </>
      )}
    </div>
  );
}
