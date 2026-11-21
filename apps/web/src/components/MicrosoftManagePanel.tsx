"use client";
import { useEffect, useState } from "react";
import { BrandTile } from "@/components/integrations";
import { MICROSOFT_SERVICES, MicrosoftConnectCard, signInResultMessage, signInWithMicrosoft } from "@/components/MicrosoftParts";
import { Card, Row, Toggle } from "@/components/WhatsAppSettingsParts";
import { buttonClass, Dropdown, ErrorState, inputClass, Loading } from "@/components/ui";
import { api, ApiError, type MicrosoftFeature, type MicrosoftSettings, type MicrosoftSettingsUpdate, type MicrosoftStatus } from "@/lib/api";
import { useApi } from "@/lib/useApi";

type TabId = "general" | "mail" | "calendar" | "onedrive" | "devops";
const TABS: { id: TabId; label: string }[] = [
  { id: "general", label: "General" }, { id: "mail", label: "Outlook Mail" }, { id: "calendar", label: "Calendar" }, { id: "onedrive", label: "OneDrive" }, { id: "devops", label: "Azure DevOps" },
];
const COUNTS = [3, 4, 5, 6, 8, 10];
const countOptions = (noun: string) => COUNTS.map((n) => ({ value: String(n), label: `${n} ${noun}` }));
const MAIL_FILTERS = [{ value: "inbox", label: "Inbox" }, { value: "unread", label: "Unread" }, { value: "flagged", label: "Flagged" }, { value: "important", label: "High importance" }];
const DAYS = [{ value: "1", label: "Today" }, { value: "3", label: "3 days" }, { value: "7", label: "7 days" }, { value: "14", label: "14 days" }];
const FILE_SOURCES = [{ value: "recent", label: "Recently used" }, { value: "shared", label: "Shared with me" }];
const WORK_SCOPES = [{ value: "assigned", label: "Assigned to me" }, { value: "created", label: "Created by me" }];

const errorText = (err: unknown, fallback: string) => (err instanceof ApiError ? err.message : fallback);

/** Azure DevOps through a personal access token: works with any account (including a personal Outlook one), unlike Microsoft sign-in. */
function DevOpsTokenCard({ org, active, disabled, onDone, onError }: { org: string | null; active: boolean; disabled: boolean; onDone: () => void; onError: (m: string | null) => void }) {
  const [organization, setOrganization] = useState(org ?? "");
  const [token, setToken] = useState("");
  const [busy, setBusy] = useState(false);
  async function save() {
    setBusy(true); onError(null);
    try { await api.setDevOpsToken(organization, token); setToken(""); onDone(); }
    catch (err) { onError(errorText(err, "Could not save the token.")); }
    finally { setBusy(false); }
  }
  async function remove() {
    setBusy(true); onError(null);
    try { await api.removeDevOpsToken(); onDone(); }
    catch (err) { onError(errorText(err, "Could not remove the token.")); }
    finally { setBusy(false); }
  }
  return (
    <section>
      <h3 className="text-base font-bold">Connect with a token</h3>
      <p className="mb-2 text-xs text-muted">Use this if Microsoft sign-in does not work for Azure DevOps, for example with a personal Outlook account. In Azure DevOps open User settings → Personal access tokens → New token, give it <strong>Work Items: Read</strong> and <strong>Project and Team: Read</strong>, and paste it here. The token is stored encrypted and never shown again.</p>
      {active ? (
        <div className="flex flex-wrap items-center gap-3 rounded-[1.25rem] border border-line p-3">
          <span className="text-sm">Using a token for <strong>{org}</strong>.</span>
          <button type="button" disabled={disabled || busy} onClick={() => void remove()} className="rounded-full border border-danger px-4 py-2 text-sm font-semibold text-danger hover:bg-danger-bg">Remove token</button>
        </div>
      ) : (
        <div className="space-y-2 rounded-[1.25rem] border border-line p-3">
          <input value={organization} onChange={(e) => setOrganization(e.target.value)} placeholder="Organization name (dev.azure.com/<name>)" aria-label="Azure DevOps organization" className={inputClass} autoComplete="off" />
          <input value={token} onChange={(e) => setToken(e.target.value)} type="password" placeholder="Personal access token" aria-label="Azure DevOps personal access token" className={inputClass} autoComplete="off" />
          <button type="button" disabled={disabled || busy || !organization.trim() || !token.trim()} onClick={() => void save()} className={`${buttonClass} px-5`}>{busy ? "Checking…" : "Save token"}</button>
        </div>
      )}
    </section>
  );
}

function Section({ title, hint, children }: { title: string; hint?: string; children: React.ReactNode }) {
  return <section><h3 className="text-base font-bold">{title}</h3>{hint && <p className="mb-2 text-xs text-muted">{hint}</p>}{!hint && <div className="mb-2" />}<Card>{children}</Card></section>;
}

function ServiceOff({ name, onEnable }: { name: string; onEnable: () => void }) {
  return (
    <div className="rounded-[1.25rem] border border-dashed border-line p-4 text-sm">
      <p className="text-muted">{name} is turned off, so its settings do not apply yet.</p>
      <button type="button" onClick={onEnable} className={`${buttonClass} mt-3 px-4`}>Turn on {name}</button>
    </div>
  );
}

/** The Microsoft "Manage" panel, laid out like Gmail's: General, then a tab of settings for each service. */
export function MicrosoftManagePanel({ onChanged }: { onChanged?: () => void }) {
  const { state, reload } = useApi(async () => {
    const [status, available] = await Promise.all([api.microsoftStatus(), api.availableDashboardApps()]);
    return { status, onDashboard: status.connectionId ? available.find((a) => a.connectionId === status.connectionId)?.dashboardId ?? null : null };
  });
  const [tab, setTab] = useState<TabId>("general");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);
  const [settings, setSettings] = useState<MicrosoftSettings | null>(null);
  const [signature, setSignature] = useState("");
  const [orgs, setOrgs] = useState<string[]>([]);
  const connected = state.status === "ready" && state.data.status.connected;

  useEffect(() => {
    if (!connected) return;
    api.microsoftSettings().then((s) => { setSettings(s); setSignature(s.signature); }, (err) => setError(errorText(err, "Could not load Microsoft settings.")));
  }, [connected]);

  // Azure DevOps organizations, only needed for the default-organization setting.
  const devOps = state.status === "ready" && state.data.status.features.includes("devops") && !state.data.status.devOpsToken;
  useEffect(() => {
    if (!devOps || tab !== "devops") return;
    let live = true;
    api.devOpsSummary().then((d) => { if (live) setOrgs(d.organizations); }, () => undefined);
    return () => { live = false; };
  }, [devOps, tab]);

  if (state.status === "loading") return <Loading label="Loading Microsoft" />;
  if (state.status === "error") return <ErrorState error={state.error} onRetry={reload} />;
  const { status, onDashboard } = state.data as { status: MicrosoftStatus; onDashboard: string | null };

  function flash(message: string) { setSaved(message); window.setTimeout(() => setSaved((c) => (c === message ? null : c)), 2500); }

  async function run(action: () => Promise<unknown>, fallback: string, message?: string) {
    setBusy(true); setError(null);
    try { await action(); if (message) flash(message); onChanged?.(); reload(); }
    catch (err) { setError(errorText(err, fallback)); }
    finally { setBusy(false); }
  }

  async function update(change: MicrosoftSettingsUpdate, message = "Saved") {
    setError(null);
    try { setSettings(await api.updateMicrosoftSettings(change)); flash(message); onChanged?.(); }
    catch (err) { setError(errorText(err, "Could not save your Microsoft settings.")); }
  }

  const askMicrosoft = async (features: MicrosoftFeature[], write: boolean) => {
    const message = signInResultMessage(await signInWithMicrosoft(features, write));
    if (message) throw new ApiError(0, message);
  };
  const toggleService = (id: MicrosoftFeature, on: boolean) =>
    on ? run(() => askMicrosoft([...status.features, id], status.write), "Could not start Microsoft sign-in.")
       : run(() => api.disableMicrosoftFeature(id), "Could not turn that service off.");
  const toggleWrite = (on: boolean) => on ? run(() => askMicrosoft(status.features, true), "Could not start Microsoft sign-in.") : run(() => api.disableMicrosoftWrite(), "Could not switch back to read-only.");
  const has = (f: MicrosoftFeature) => status.features.includes(f);

  return (
    <div>
      <header className="flex flex-wrap items-center gap-3 pr-10">
        <BrandTile icon="mono:MS" color="#0078D4" size={44} />
        <div className="min-w-0">
          <h2 className="text-xl font-bold">Microsoft</h2>
          <p className="truncate text-xs text-muted">{status.connected ? `Connected as ${status.email}` : "Not connected"}</p>
        </div>
      </header>

      {!status.configured && <p role="alert" className="mt-4 rounded-xl border border-line bg-surface-2 p-3 text-sm">Microsoft sign-in is not set up on the server yet. An administrator needs to add the Microsoft app registration (see docs/integrations/microsoft.md).</p>}
      {status.error && <p role="alert" className="mt-4 text-sm text-danger">{status.error}</p>}

      {!status.connected ? (
        <div className="mt-5">
          <MicrosoftConnectCard already={[]} onConnected={() => { onChanged?.(); reload(); }} />
          {error && <p role="alert" className="mt-3 text-sm text-danger">{error}</p>}
        </div>
      ) : (
        <>
          <div role="tablist" aria-label="Microsoft settings" className="sticky top-0 z-10 -mx-1 mt-5 flex gap-1 overflow-x-auto border-b border-line bg-surface px-1">
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
                <Section title="Services" hint="One Microsoft sign-in covers all of these. Turn on only what you want Daksha to use, and to answer your questions from.">
                  {MICROSOFT_SERVICES.map((s) => (
                    <Row key={s.id} label={s.name} hint={s.description} search="">
                      <Toggle label={s.name} checked={has(s.id)} disabled={busy} onChange={(on) => void toggleService(s.id, on)} />
                    </Row>
                  ))}
                </Section>
                <Section title="Access level" hint="Daksha starts read-only, which is easier for a company to approve. Full access lets it send mail and change events and files for you.">
                  <Row label="Full access" hint="Send mail, add and edit calendar events, change files." search="">
                    <Toggle label="Full access" checked={status.write} disabled={busy} onChange={(on) => void toggleWrite(on)} />
                  </Row>
                </Section>
                <Section title="Dashboard" hint="Show an overview tile with your unread mail, next events, recent files and work items. What it shows is set in each service's tab.">
                  <Row label="Show Microsoft dashboard" search="">
                    <Toggle label="Show Microsoft dashboard" checked={onDashboard !== null} disabled={busy}
                      onChange={(on) => void run(() => (on ? api.addToDashboard(status.connectionId!) : api.removeFromDashboard(onDashboard!)), "Could not change the dashboard.", on ? "Microsoft added to your dashboard" : "Microsoft removed from your dashboard")} />
                  </Row>
                </Section>
                <section>
                  <h3 className="text-base font-bold">Account</h3>
                  <p className="mb-2 text-xs text-muted">{`Signed in as ${status.displayName ?? status.email}.`}</p>
                  <div className="flex flex-wrap gap-2 rounded-[1.25rem] border border-line p-3">
                    <a href="/apps/microsoft" className={`${buttonClass} px-4`}>Open Microsoft</a>
                    <button type="button" disabled={busy} className="rounded-full border border-danger px-4 py-2 text-sm font-semibold text-danger hover:bg-danger-bg"
                      onClick={() => { if (window.confirm("Disconnect your Microsoft account from Daksh?")) void run(() => api.disconnectMicrosoft(), "Could not disconnect."); }}>Disconnect</button>
                  </div>
                </section>
              </>
            )}

            {tab === "mail" && (!has("mail") ? <ServiceOff name="Outlook Mail" onEnable={() => void toggleService("mail", true)} /> : !settings ? <Loading label="Loading settings" /> : (
              <>
                <Section title="Reading">
                  <Row label="Show images in emails" hint="Off by default: remote images stay blocked when you read an email (this also stops tracking pixels)." search="">
                    <Toggle label="Show images in emails" checked={settings.showImages} onChange={(v) => void update({ showImages: v })} />
                  </Row>
                </Section>
                <section className="rounded-[1.25rem] border border-line p-4">
                  <label htmlFor="ms-signature" className="text-sm font-semibold">Signature</label>
                  <p className="mt-0.5 text-xs text-muted">Added to the end of every email you send from Daksha.{!status.write && " Sending needs full access (General tab)."}</p>
                  <textarea id="ms-signature" value={signature} onChange={(e) => setSignature(e.target.value)} rows={5} maxLength={2000} className={`${inputClass} mt-2 resize-y`} placeholder={"Best regards,\nYour name"} />
                  <button onClick={() => void update({ signature }, "Signature saved")} disabled={signature === settings.signature} className={`${buttonClass} mt-3`}>Save signature</button>
                </section>
                <Section title="Dashboard">
                  <Row label="Emails to show" hint="How many emails the dashboard tile lists." search="">
                    <Dropdown className="w-36" value={String(settings.mailCount)} options={countOptions("emails")} onChange={(v) => void update({ mailCount: Number(v) })} />
                  </Row>
                  <Row label="Which emails" hint="The mail the dashboard tile previews." search="">
                    <Dropdown className="w-52" value={settings.mailFilter} options={MAIL_FILTERS} onChange={(v) => void update({ mailFilter: v as MicrosoftSettings["mailFilter"] })} />
                  </Row>
                </Section>
              </>
            ))}

            {tab === "calendar" && (!has("calendar") ? <ServiceOff name="Outlook Calendar" onEnable={() => void toggleService("calendar", true)} /> : !settings ? <Loading label="Loading settings" /> : (
              <>
                <Section title="Calendar" hint="Your Outlook events appear in Daksha's Calendar together with Google. Adding and editing events needs full access (General tab).">
                  <Row label="Open Calendar" hint="See, add and edit events." search=""><a href="/calendar" className={`${buttonClass} px-4`}>Open</a></Row>
                </Section>
                <Section title="Dashboard">
                  <Row label="Look ahead" hint="How far the dashboard tile shows events." search="">
                    <Dropdown className="w-36" value={String(settings.calendarDays)} options={DAYS} onChange={(v) => void update({ calendarDays: Number(v) as MicrosoftSettings["calendarDays"] })} />
                  </Row>
                  <Row label="Events to show" hint="How many events the tile lists." search="">
                    <Dropdown className="w-36" value={String(settings.calendarCount)} options={countOptions("events")} onChange={(v) => void update({ calendarCount: Number(v) })} />
                  </Row>
                  <Row label="Show all-day events" hint="Birthdays, holidays and other all-day entries." search="">
                    <Toggle label="Show all-day events" checked={settings.calendarShowAllDay} onChange={(v) => void update({ calendarShowAllDay: v })} />
                  </Row>
                </Section>
              </>
            ))}

            {tab === "onedrive" && (!has("onedrive") ? <ServiceOff name="OneDrive" onEnable={() => void toggleService("onedrive", true)} /> : !settings ? <Loading label="Loading settings" /> : (
              <>
                <Section title="Files" hint="Daksha can browse and search your OneDrive. It only reads files; changing them needs full access (General tab).">
                  <Row label="Open OneDrive" hint="Browse and search your files." search=""><a href="/apps/microsoft?tab=onedrive" className={`${buttonClass} px-4`}>Open</a></Row>
                </Section>
                <Section title="Dashboard">
                  <Row label="Files to show" hint="How many files the dashboard tile lists." search="">
                    <Dropdown className="w-36" value={String(settings.fileCount)} options={countOptions("files")} onChange={(v) => void update({ fileCount: Number(v) })} />
                  </Row>
                  <Row label="Which files" hint="What the dashboard tile previews." search="">
                    <Dropdown className="w-52" value={settings.fileSource} options={FILE_SOURCES} onChange={(v) => void update({ fileSource: v as MicrosoftSettings["fileSource"] })} />
                  </Row>
                </Section>
              </>
            ))}

            {tab === "devops" && (!has("devops") ? (
              <>
                <ServiceOff name="Azure DevOps" onEnable={() => void toggleService("devops", true)} />
                <DevOpsTokenCard key="off" org={status.devOpsOrg} active={false} disabled={busy} onDone={() => { onChanged?.(); reload(); }} onError={setError} />
              </>
            ) : !settings ? <Loading label="Loading settings" /> : (
              <>
                <Section title="Work items">
                  <Row label="Which work items" hint="What Daksha lists and answers questions about." search="">
                    <Dropdown className="w-52" value={settings.workItemScope} options={WORK_SCOPES} onChange={(v) => void update({ workItemScope: v as MicrosoftSettings["workItemScope"] })} />
                  </Row>
                  <Row label="Include closed items" hint="Off: only items that are still open." search="">
                    <Toggle label="Include closed items" checked={settings.workItemIncludeClosed} onChange={(v) => void update({ workItemIncludeClosed: v })} />
                  </Row>
                  {orgs.length > 1 && (
                    <Row label="Default organization" hint="Used when you have more than one." search="">
                      <Dropdown className="w-52" value={settings.devOpsDefaultOrg ?? orgs[0]} options={orgs.map((o) => ({ value: o, label: o }))} onChange={(v) => void update({ devOpsDefaultOrg: v })} />
                    </Row>
                  )}
                </Section>
                <Section title="Dashboard">
                  <Row label="Work items to show" hint="How many items the dashboard tile lists." search="">
                    <Dropdown className="w-36" value={String(settings.workItemCount)} options={countOptions("items")} onChange={(v) => void update({ workItemCount: Number(v) })} />
                  </Row>
                </Section>
                <DevOpsTokenCard key={`${status.devOpsToken}-${status.devOpsOrg}`} org={status.devOpsOrg} active={status.devOpsToken} disabled={busy} onDone={() => { onChanged?.(); reload(); }} onError={setError} />
              </>
            ))}
          </div>
        </>
      )}
    </div>
  );
}
