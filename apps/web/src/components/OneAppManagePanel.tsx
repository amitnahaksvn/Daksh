"use client";
import { useEffect, useState } from "react";
import { BrandTile } from "@/components/integrations";
import { OneAppConnectCard, oneAppCallbackError, signInOneApp, type OneAppConfig, type OneAppSettingRow } from "@/components/OneAppParts";
import { Card, Row, Toggle } from "@/components/WhatsAppSettingsParts";
import { buttonClass, Dropdown, ErrorState, inputClass, Loading } from "@/components/ui";
import { api, ApiError, type OneAppSettings, type OneAppStatus } from "@/lib/api";
import { useApi } from "@/lib/useApi";

const errorText = (err: unknown, fallback: string) => (err instanceof ApiError ? err.message : fallback);
const COUNTS = [3, 4, 5, 6, 8, 10];

function Section({ title, hint, children }: { title: string; hint?: string; children: React.ReactNode }) {
  return <section><h3 className="text-base font-bold">{title}</h3>{hint && <p className="mb-2 text-xs text-muted">{hint}</p>}{!hint && <div className="mb-2" />}<Card>{children}</Card></section>;
}

function TextSetting({ row, value, onSave }: { row: OneAppSettingRow; value: string; onSave: (v: string) => void }) {
  const [text, setText] = useState(value);
  return (
    <div className="flex w-full max-w-sm gap-2">
      <input value={text} onChange={(e) => setText(e.target.value)} aria-label={row.label} placeholder={row.placeholder} className={inputClass} />
      <button type="button" disabled={text === value} onClick={() => onSave(text)} className={`${buttonClass} px-4`}>Save</button>
    </div>
  );
}

/**
 * The "Manage" panel for GitHub, Notion, Discord and Spotify, laid out like Gmail's and Microsoft's: connect, or a General tab
 * (account, access level, dashboard, disconnect) plus a tab of settings for each part of the app.
 */
export function OneAppManagePanel({ config, onChanged }: { config: OneAppConfig; onChanged?: () => void }) {
  const { state, reload } = useApi(async () => {
    const [status, available] = await Promise.all([api.oneAppStatus(config.key), api.availableDashboardApps()]);
    return { status, onDashboard: status.connectionId ? available.find((a) => a.connectionId === status.connectionId)?.dashboardId ?? null : null };
  });
  const [tab, setTab] = useState("general");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);
  const [settings, setSettings] = useState<OneAppSettings | null>(null);
  const connected = state.status === "ready" && state.data.status.connected;

  useEffect(() => {
    if (!connected) return;
    api.oneAppSettings(config.key).then(setSettings, (err) => setError(errorText(err, `Could not load ${config.name} settings.`)));
  }, [connected, config.key, config.name]);

  if (state.status === "loading") return <Loading label={`Loading ${config.name}`} />;
  if (state.status === "error") return <ErrorState error={state.error} onRetry={reload} />;
  const { status, onDashboard } = state.data as { status: OneAppStatus; onDashboard: string | null };

  function flash(message: string) { setSaved(message); window.setTimeout(() => setSaved((c) => (c === message ? null : c)), 2500); }

  async function run(action: () => Promise<unknown>, fallback: string, message?: string) {
    setBusy(true); setError(null);
    try { await action(); if (message) flash(message); onChanged?.(); reload(); }
    catch (err) { setError(errorText(err, fallback)); }
    finally { setBusy(false); }
  }

  async function update(change: OneAppSettings, message = "Saved") {
    setError(null);
    try { setSettings(await api.updateOneAppSettings(config.key, change)); flash(message); onChanged?.(); }
    catch (err) { setError(errorText(err, `Could not save your ${config.name} settings.`)); }
  }

  const grantedOptions = (config.options ?? []).filter((o) => o.granted(status)).map((o) => o.id);
  const signIn = (extra: Record<string, boolean>) => run(async () => {
    const params: Record<string, string | boolean> = { write: status.write, ...Object.fromEntries(grantedOptions.map((id) => [id, true])), ...extra };
    const result = await signInOneApp(config.key, params);
    if (result !== "connected" && result !== "closed") throw new ApiError(0, oneAppCallbackError(config.name, result));
  }, `Could not start ${config.name} sign-in.`);
  const toggleWrite = (on: boolean) => (on && config.writeNeedsSignIn ? signIn({ write: true }) : run(() => api.setOneAppWrite(config.key, on), "Could not change full access."));

  function renderRow(row: OneAppSettingRow) {
    if (!settings) return null;
    const value = settings[row.key];
    return (
      <Row key={row.key} label={row.label} hint={row.hint} search="">
        {row.type === "bool" && <Toggle label={row.label} checked={value === true} onChange={(v) => void update({ [row.key]: v })} />}
        {row.type === "count" && <Dropdown className="w-36" value={String(value)} options={COUNTS.map((n) => ({ value: String(n), label: `${n} ${row.noun ?? "items"}` }))} onChange={(v) => void update({ [row.key]: Number(v) })} />}
        {row.type === "enum" && <Dropdown className="w-56" value={String(value)} options={row.options ?? []} onChange={(v) => void update({ [row.key]: v })} />}
        {row.type === "text" && <TextSetting key={String(value)} row={row} value={String(value ?? "")} onSave={(v) => void update({ [row.key]: v })} />}
      </Row>
    );
  }

  return (
    <div>
      <header className="flex flex-wrap items-center gap-3 pr-10">
        <BrandTile icon={config.icon} color={config.color} size={44} />
        <div className="min-w-0">
          <h2 className="text-xl font-bold">{config.name}</h2>
          <p className="truncate text-xs text-muted">{status.connected ? `Connected as ${status.accountName}` : "Not connected"}</p>
        </div>
      </header>

      {!status.configured && !config.connect && <p role="alert" className="mt-4 rounded-xl border border-line bg-surface-2 p-3 text-sm">{config.name} sign-in is not set up on the server yet. An administrator needs to add the app credentials (see {config.setupDoc}).</p>}
      {status.error && <p role="alert" className="mt-4 text-sm text-danger">{status.error}</p>}

      {!status.connected ? (
        <div className="mt-5">
          {config.connect ? config.connect(() => { onChanged?.(); reload(); }) : <OneAppConnectCard config={config} onConnected={() => { onChanged?.(); reload(); }} />}
        </div>
      ) : (
        <>
          <div role="tablist" aria-label={`${config.name} settings`} className="sticky top-0 z-10 -mx-1 mt-5 flex gap-1 overflow-x-auto border-b border-line bg-surface px-1">
            {[{ id: "general", label: "General" }, ...config.tabs].map((t) => (
              <button key={t.id} role="tab" aria-selected={tab === t.id} onClick={() => setTab(t.id)}
                className={`shrink-0 border-b-2 px-4 py-2 text-sm font-semibold transition-colors ${tab === t.id ? "border-accent text-text" : "border-transparent text-muted hover:text-text"}`}>{t.label}</button>
            ))}
          </div>

          <div className="mt-4 space-y-4">
            {error && <p role="alert" className="rounded-[1.25rem] bg-danger-bg p-3 text-sm text-danger">{error}</p>}
            {saved && <p role="status" className="rounded-[1.25rem] bg-lime p-3 text-sm text-on-tint">{saved}</p>}

            {tab === "general" && (
              <>
                {config.hideWrite ? <p className="rounded-[1.25rem] border border-line bg-surface-2 p-3 text-sm text-muted">{config.name} is read-only: Daksha looks things up and never changes anything.</p> : (
                <Section title="Access level" hint={`${config.name} starts read-only. ${config.writeHint}`}>
                  <Row label={config.writeLabel} hint={config.writeNeedsSignIn ? `${config.name} asks you to approve this when you turn it on.` : undefined} search="">
                    <Toggle label={config.writeLabel} checked={status.write} disabled={busy} onChange={(v) => void toggleWrite(v)} />
                  </Row>
                  {config.options?.map((o) => (
                    <Row key={o.id} label={o.label} hint={o.granted(status) ? `${o.hint} To take it back, remove the access in ${config.name}'s own settings.` : o.hint} search="">
                      <Toggle label={o.label} checked={o.granted(status)} disabled={busy || o.granted(status)} onChange={(v) => { if (v) void signIn({ [o.id]: true }); }} />
                    </Row>
                  ))}
                </Section>)}
                <Section title="Dashboard" hint={`Show a ${config.name} tile on your Daksha dashboard. What it shows is set in the other tabs.`}>
                  <Row label={`Show ${config.name} on dashboard`} search="">
                    <Toggle label={`Show ${config.name} on dashboard`} checked={onDashboard !== null} disabled={busy}
                      onChange={(v) => void run(() => (v ? api.addToDashboard(status.connectionId!) : api.removeFromDashboard(onDashboard!)), "Could not change the dashboard.", v ? `${config.name} added to your dashboard` : `${config.name} removed from your dashboard`)} />
                  </Row>
                </Section>
                {config.extraGeneral?.(status, () => { onChanged?.(); reload(); })}
                <section>
                  <h3 className="text-base font-bold">Account</h3>
                  <p className="mb-2 text-xs text-muted">Connected as {status.accountName}.</p>
                  <div className="flex flex-wrap gap-2 rounded-[1.25rem] border border-line p-3">
                    <a href={config.pageHref} className={`${buttonClass} px-4`}>Open {config.name}</a>
                    <button type="button" disabled={busy} className="rounded-full border border-danger px-4 py-2 text-sm font-semibold text-danger hover:bg-danger-bg"
                      onClick={() => { if (window.confirm(`Disconnect ${config.name} from Daksh?`)) void run(() => api.disconnectOneApp(config.key), "Could not disconnect."); }}>Disconnect</button>
                  </div>
                </section>
              </>
            )}

            {config.tabs.filter((t) => t.id === tab).map((t) => (
              !settings ? <Loading key={t.id} label="Loading settings" /> : (
                <div key={t.id} className="space-y-4">{t.sections.map((s) => <Section key={s.title} title={s.title} hint={s.hint}>{s.rows.map(renderRow)}</Section>)}</div>
              )
            ))}
          </div>
        </>
      )}
    </div>
  );
}
