"use client";
import { useEffect, useState } from "react";
import { BrandTile } from "@/components/integrations";
import { GOOGLE_SERVICES, GoogleConnectCard, turnOnGoogleServices } from "@/components/GoogleWorkspaceParts";
import { Card, Row, Toggle } from "@/components/WhatsAppSettingsParts";
import { buttonClass, Dropdown, ErrorState, Loading } from "@/components/ui";
import { api, ApiError, type GoogleService, type GoogleWorkspaceSettings, type GoogleWorkspaceSettingsUpdate, type GoogleWorkspaceStatus } from "@/lib/api";
import { useApi } from "@/lib/useApi";

type TabId = "general" | GoogleService;
const TABS: { id: TabId; label: string }[] = [
  { id: "general", label: "General" }, { id: "drive", label: "Drive" }, { id: "docs", label: "Docs" }, { id: "sheets", label: "Sheets" }, { id: "slides", label: "Slides" },
];
const COUNTS = [3, 4, 5, 6, 8, 10].map((n) => ({ value: String(n), label: `${n} files` }));
const SOURCES = [{ value: "recent", label: "Recently opened" }, { value: "mine", label: "My Drive" }, { value: "shared", label: "Shared with me" }, { value: "starred", label: "Starred" }];
const SORTS = [{ value: "modified", label: "Most recent first" }, { value: "name", label: "Name (A to Z)" }];
const OPEN_IN = [{ value: "daksha", label: "In Daksha" }, { value: "google", label: "In Google" }];
const ROWS = [10, 25, 50, 100].map((n) => ({ value: String(n), label: `${n} rows` }));

const errorText = (err: unknown, fallback: string) => (err instanceof ApiError ? err.message : fallback);

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

/** The Google Drive, Docs, Sheets and Slides "Manage" panel, laid out like Gmail's and Microsoft's: General, then a tab of settings for each service. */
export function GoogleWorkspaceManagePanel({ onChanged, initialTab }: { onChanged?: () => void; initialTab?: TabId }) {
  const { state, reload } = useApi(async () => {
    const [status, available] = await Promise.all([api.googleStatus(), api.availableDashboardApps()]);
    return { status, onDashboard: status.connectionId ? available.find((a) => a.connectionId === status.connectionId)?.dashboardId ?? null : null };
  });
  const [tab, setTab] = useState<TabId>(initialTab ?? "general");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);
  const [settings, setSettings] = useState<GoogleWorkspaceSettings | null>(null);
  const connected = state.status === "ready" && state.data.status.connected;

  useEffect(() => {
    if (!connected) return;
    api.googleSettings().then(setSettings, (err) => setError(errorText(err, "Could not load Google settings.")));
  }, [connected]);

  if (state.status === "loading") return <Loading label="Loading Google" />;
  if (state.status === "error") return <ErrorState error={state.error} onRetry={reload} />;
  const { status, onDashboard } = state.data as { status: GoogleWorkspaceStatus; onDashboard: string | null };
  const on = (s: GoogleService) => status.services.find((x) => x.id === s)?.enabled ?? false;

  function flash(message: string) { setSaved(message); window.setTimeout(() => setSaved((c) => (c === message ? null : c)), 2500); }

  async function run(action: () => Promise<unknown>, fallback: string, message?: string) {
    setBusy(true); setError(null);
    try { await action(); if (message) flash(message); onChanged?.(); reload(); }
    catch (err) { setError(errorText(err, fallback)); }
    finally { setBusy(false); }
  }

  async function update(change: GoogleWorkspaceSettingsUpdate, message = "Saved") {
    setError(null);
    try { setSettings(await api.updateGoogleSettings(change)); flash(message); onChanged?.(); }
    catch (err) { setError(errorText(err, "Could not save your Google settings.")); }
  }

  const turnOn = (services: GoogleService[], write?: boolean) => run(async () => { const m = await turnOnGoogleServices(services, write); if (m) throw new ApiError(0, m); }, "Could not turn Google on.");
  const toggleService = (id: GoogleService, enable: boolean) => enable ? turnOn([id], status.write ? true : undefined) : run(() => api.disableGoogleService(id), "Could not turn that service off.");
  const toggleWrite = (enable: boolean) => enable
    ? run(async () => { const m = await turnOnGoogleServices(status.services.filter((s) => s.enabled).map((s) => s.id), true); if (m) throw new ApiError(0, m); await api.setGoogleWrite(true); }, "Could not turn on full access.")
    : run(() => api.setGoogleWrite(false), "Could not switch back to read-only.");

  return (
    <div>
      <header className="flex flex-wrap items-center gap-3 pr-10">
        <BrandTile icon="SiGoogledrive" color="#4285F4" size={44} />
        <div className="min-w-0">
          <h2 className="text-xl font-bold">Google Drive, Docs &amp; Sheets</h2>
          <p className="truncate text-xs text-muted">{status.connected ? `Connected as ${status.email}` : status.googleConnected ? `Signed in as ${status.email}, not turned on` : "Not connected"}</p>
        </div>
      </header>

      {!status.configured && <p role="alert" className="mt-4 rounded-xl border border-line bg-surface-2 p-3 text-sm">Google sign-in is not set up on the server yet. An administrator needs to add the Google OAuth client (see docs/integrations/google-workspace.md).</p>}

      {!status.connected ? (
        <div className="mt-5">
          <GoogleConnectCard onConnected={() => { onChanged?.(); reload(); }} />
        </div>
      ) : (
        <>
          <div role="tablist" aria-label="Google settings" className="sticky top-0 z-10 -mx-1 mt-5 flex gap-1 overflow-x-auto border-b border-line bg-surface px-1">
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
                <Section title="Services" hint="One Google sign-in covers all of these, and Gmail and Calendar too. Turn on only what you want Daksha to use, and to answer your questions from.">
                  {GOOGLE_SERVICES.map((s) => (
                    <Row key={s.id} label={s.name} hint={s.description} search="">
                      <Toggle label={s.name} checked={on(s.id)} disabled={busy} onChange={(v) => void toggleService(s.id, v)} />
                    </Row>
                  ))}
                </Section>
                <Section title="Access level" hint="Daksha starts read-only. Full access lets it create documents and spreadsheets and add text and rows. It never deletes anything.">
                  <Row label="Full access" hint="Create Docs and Sheets, add text to Docs, add rows to Sheets." search="">
                    <Toggle label="Full access" checked={status.write} disabled={busy} onChange={(v) => void toggleWrite(v)} />
                  </Row>
                </Section>
                <Section title="Dashboard" hint="Show your files on the Daksha dashboard. What it shows is set in the Drive tab.">
                  <Row label="Show Google Drive on dashboard" search="">
                    <Toggle label="Show Google Drive on dashboard" checked={onDashboard !== null} disabled={busy}
                      onChange={(v) => void run(() => (v ? api.addToDashboard(status.connectionId!) : api.removeFromDashboard(onDashboard!)), "Could not change the dashboard.", v ? "Google Drive added to your dashboard" : "Google Drive removed from your dashboard")} />
                  </Row>
                </Section>
                <section>
                  <h3 className="text-base font-bold">Account</h3>
                  <p className="mb-2 text-xs text-muted">Signed in as {status.email}. Turning Google Drive off here does not sign you out of Gmail or Calendar.</p>
                  <div className="flex flex-wrap gap-2 rounded-[1.25rem] border border-line p-3">
                    <a href="/apps/google-drive" className={`${buttonClass} px-4`}>Open Google Drive</a>
                    <button type="button" disabled={busy} className="rounded-full border border-danger px-4 py-2 text-sm font-semibold text-danger hover:bg-danger-bg"
                      onClick={() => { if (window.confirm("Turn off Google Drive, Docs, Sheets and Slides in Daksh? Gmail and Calendar are not affected.")) void run(() => api.disconnectGoogleWorkspace(), "Could not turn Google off."); }}>Turn everything off</button>
                  </div>
                </section>
              </>
            )}

            {tab === "drive" && (!on("drive") ? <ServiceOff name="Google Drive" onEnable={() => void toggleService("drive", true)} /> : !settings ? <Loading label="Loading settings" /> : (
              <>
                <Section title="Files" hint="Daksha can browse and search your Drive and answer questions about your files. It only reads them unless full access is on.">
                  <Row label="Open Google Drive" hint="Browse and search your files." search=""><a href="/apps/google-drive" className={`${buttonClass} px-4`}>Open</a></Row>
                  <Row label="Sort files" hint="The order files are listed in." search="">
                    <Dropdown className="w-52" value={settings.sortBy} options={SORTS} onChange={(v) => void update({ sortBy: v as GoogleWorkspaceSettings["sortBy"] })} />
                  </Row>
                </Section>
                <Section title="Dashboard">
                  <Row label="Files to show" hint="How many files the dashboard tile lists." search="">
                    <Dropdown className="w-36" value={String(settings.fileCount)} options={COUNTS} onChange={(v) => void update({ fileCount: Number(v) })} />
                  </Row>
                  <Row label="Which files" hint="What the dashboard tile previews." search="">
                    <Dropdown className="w-52" value={settings.fileSource} options={SOURCES} onChange={(v) => void update({ fileSource: v as GoogleWorkspaceSettings["fileSource"] })} />
                  </Row>
                </Section>
              </>
            ))}

            {tab === "docs" && (!on("docs") ? <ServiceOff name="Google Docs" onEnable={() => void toggleService("docs", true)} /> : !settings ? <Loading label="Loading settings" /> : (
              <Section title="Documents" hint="Docs open from the Drive list. Adding text to a document needs full access (General tab).">
                <Row label="Open documents" hint="Where a Google Doc opens when you click it." search="">
                  <Dropdown className="w-40" value={settings.openIn} options={OPEN_IN} onChange={(v) => void update({ openIn: v as GoogleWorkspaceSettings["openIn"] })} />
                </Row>
                <Row label="Your documents" hint="See your Docs." search=""><a href="/apps/google-drive?tab=docs" className={`${buttonClass} px-4`}>Open</a></Row>
              </Section>
            ))}

            {tab === "sheets" && (!on("sheets") ? <ServiceOff name="Google Sheets" onEnable={() => void toggleService("sheets", true)} /> : !settings ? <Loading label="Loading settings" /> : (
              <Section title="Spreadsheets" hint="Sheets open from the Drive list. Adding rows needs full access (General tab).">
                <Row label="Open spreadsheets" hint="Where a Google Sheet opens when you click it." search="">
                  <Dropdown className="w-40" value={settings.openIn} options={OPEN_IN} onChange={(v) => void update({ openIn: v as GoogleWorkspaceSettings["openIn"] })} />
                </Row>
                <Row label="Rows to preview" hint="How many rows Daksha reads from a sheet at once." search="">
                  <Dropdown className="w-36" value={String(settings.sheetRows)} options={ROWS} onChange={(v) => void update({ sheetRows: Number(v) as GoogleWorkspaceSettings["sheetRows"] })} />
                </Row>
                <Row label="Your spreadsheets" hint="See your Sheets." search=""><a href="/apps/google-drive?tab=sheets" className={`${buttonClass} px-4`}>Open</a></Row>
              </Section>
            ))}

            {tab === "slides" && (!on("slides") ? <ServiceOff name="Google Slides" onEnable={() => void toggleService("slides", true)} /> : !settings ? <Loading label="Loading settings" /> : (
              <Section title="Presentations" hint="Daksha reads the text of your slides. It cannot edit presentations.">
                <Row label="Open presentations" hint="Where a Google Slides file opens when you click it." search="">
                  <Dropdown className="w-40" value={settings.openIn} options={OPEN_IN} onChange={(v) => void update({ openIn: v as GoogleWorkspaceSettings["openIn"] })} />
                </Row>
                <Row label="Your presentations" hint="See your Slides." search=""><a href="/apps/google-drive?tab=slides" className={`${buttonClass} px-4`}>Open</a></Row>
              </Section>
            ))}
          </div>
        </>
      )}
    </div>
  );
}
