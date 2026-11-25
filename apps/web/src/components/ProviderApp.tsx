"use client";
import { useEffect, useMemo, useState } from "react";
import type { FormEvent, ReactNode } from "react";
import { Modal } from "@/components/Modal";
import { OneAppDashboardCard, OneAppShell, oneAppCallbackError, signInOneApp, Rows } from "@/components/OneAppParts";
import type { OneAppConfig, OneAppSettingRow } from "@/components/OneAppParts";
import { OneAppManagePanel } from "@/components/OneAppManagePanel";
import { buttonClass, Dropdown, EmptyState, ErrorState, inputClass, Loading, PageHeader, tileClass } from "@/components/ui";
import { api, ApiError, type DashboardApp, type OneAppStatus, type ProviderAction, type ProviderAppKey, type ProviderInput, type ProviderItem, type ProviderSpec, type ProviderView } from "@/lib/api";
import { useApi } from "@/lib/useApi";

/**
 * Every app built on the provider engine (YouTube, Instagram, Shopify, Home Assistant ...) renders from its server-side spec:
 * the connect form, one tab per list, a tab of actions, and the Manage popup. Nothing here is specific to one app.
 */

const errorText = (err: unknown, fallback: string) => (err instanceof ApiError ? err.message : fallback);
const defaults = (inputs: ProviderInput[]) => Object.fromEntries(inputs.map((i) => [i.key, i.default ?? ""]));

// ---- spec → the Manage popup's configuration ---------------------------------------------------

export function configFromSpec(spec: ProviderSpec, onConnected?: () => void): OneAppConfig {
  const rows: OneAppSettingRow[] = spec.settings.map((s) => ({
    key: s.key, label: s.label, hint: s.hint ?? undefined, type: s.type, placeholder: s.placeholder ?? undefined, noun: "items",
    options: s.type === "enum" ? (s.options ?? []).map((o) => ({ value: o, label: s.key === "dashboardView" ? (spec.views.find((v) => v.id === o)?.label ?? o) : o })) : undefined,
  }));
  const dashboardRows = rows.filter((r) => r.key === "dashboardView" || r.key === "itemCount");
  const otherRows = rows.filter((r) => r.key !== "dashboardView" && r.key !== "itemCount");
  const tabs: OneAppConfig["tabs"] = [{ id: "dashboard", label: "Dashboard", sections: [{ title: "Dashboard tile", hint: `What the ${spec.name} tile on your dashboard shows.`, rows: dashboardRows }] }];
  if (otherRows.length > 0) tabs.push({ id: "preferences", label: "Preferences", sections: [{ title: "Preferences", rows: otherRows }] });
  return {
    key: spec.key, name: spec.name, icon: spec.icon, color: spec.color, pageHref: `/apps/${spec.key}`, account: spec.account,
    connectTitle: spec.connectTitle, connectBlurb: spec.connectBlurb, connectLabel: spec.connectLabel,
    writeLabel: spec.writeLabel, writeHint: spec.writeHint, writeNeedsSignIn: spec.writeNeedsSignIn, hideWrite: !spec.hasWrite,
    tabs, setupDoc: spec.setupDoc,
    connect: (done) => <ProviderConnectCard spec={spec} onConnected={() => { onConnected?.(); done(); }} />,
  };
}

// ---- connecting --------------------------------------------------------------------------------

/** One field of a form: text-like inputs, a dropdown, or a dropdown whose choices come from a list of the same app. */
export function InputField({ appKey, input, value, onChange, idPrefix }: { appKey: ProviderAppKey; input: ProviderInput; value: string; onChange: (v: string) => void; idPrefix: string }) {
  const id = `${idPrefix}-${input.key}`;
  const [choices, setChoices] = useState<ProviderItem[] | null>(null);
  const [choiceError, setChoiceError] = useState<string | null>(null);
  useEffect(() => {
    if (input.kind !== "select-view" || !input.optionsView) return;
    let live = true;
    const t = setTimeout(() => { api.providerView(appKey, input.optionsView!).then((r) => live && setChoices(r), (e) => live && setChoiceError(errorText(e, "Could not load the choices."))); }, 0);
    return () => { live = false; clearTimeout(t); };
  }, [appKey, input.kind, input.optionsView]);

  let control: ReactNode;
  if (input.kind === "select") control = <Dropdown value={value} options={(input.options ?? []).map((o) => ({ value: o, label: o }))} onChange={onChange} />;
  else if (input.kind === "select-view") control = choiceError ? <p role="alert" className="text-xs text-danger">{choiceError}</p> : choices === null ? <p className="text-xs text-muted">Loading choices…</p> : choices.length === 0 ? <p className="text-xs text-muted">Nothing to choose from yet.</p> : <Dropdown value={value} options={choices.map((c) => ({ value: c.id, label: c.title }))} onChange={onChange} searchable />;
  else if (input.kind === "textarea") control = <textarea id={id} value={value} onChange={(e) => onChange(e.target.value)} rows={3} placeholder={input.placeholder ?? undefined} className={`${inputClass} resize-y`} />;
  else {
    const type = input.kind === "secret" ? "password" : input.kind === "number" ? "number" : input.kind === "date" ? "date" : input.kind === "datetime" ? "datetime-local" : input.kind === "url" ? "url" : "text";
    control = <input id={id} type={type} value={value} onChange={(e) => onChange(e.target.value)} placeholder={input.placeholder ?? undefined} autoComplete="off" className={inputClass} />;
  }
  return (
    <div>
      <label htmlFor={id} className="mb-1 block text-sm font-semibold">{input.label}{input.required ? "" : <span className="font-normal text-muted"> (optional)</span>}</label>
      {control}
      {input.hint && <p className="mt-1 text-xs text-muted">{input.hint}</p>}
    </div>
  );
}

/** The sign-in card: the details the app needs, then a button (an OAuth popup, a credential check, or just turning it on). */
export function ProviderConnectCard({ spec, onConnected }: { spec: ProviderSpec; onConnected: () => void }) {
  const { state } = useApi(() => api.oneAppStatus(spec.key));
  const [values, setValues] = useState<Record<string, string>>(() => defaults(spec.connectFields));
  const [full, setFull] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const missing = spec.connectFields.some((f) => f.required && !values[f.key]?.trim());
  const notSetUp = state.status === "ready" && !state.data.configured && spec.auth !== "credentials";

  async function connect(e?: FormEvent) {
    e?.preventDefault();
    setError(null); setBusy(true);
    try {
      if (spec.auth === "oauth") {
        const result = await signInOneApp(spec.key, { write: full, ...values });
        if (result === "connected") onConnected();
        else if (result !== "closed") setError(oneAppCallbackError(spec.name, result));
      } else {
        await api.connectProviderApp(spec.key, values);
        onConnected();
      }
    } catch (err) { setError(errorText(err, `Could not connect ${spec.name}.`)); }
    finally { setBusy(false); }
  }

  return (
    <form onSubmit={(e) => void connect(e)} className={`space-y-3 p-5 ${tileClass}`}>
      <h3 className="text-lg font-bold">{spec.connectTitle}</h3>
      <p className="text-sm text-muted">{spec.connectBlurb}</p>
      {notSetUp && <p role="status" className="rounded-xl border border-line bg-surface-2 p-3 text-sm">{spec.name} is not set up on the server yet. An administrator needs to add its keys (see {spec.setupDoc}).</p>}
      {spec.connectFields.map((f) => <InputField key={f.key} appKey={spec.key} input={f} value={values[f.key] ?? ""} onChange={(v) => setValues((c) => ({ ...c, [f.key]: v }))} idPrefix={`connect-${spec.key}`} />)}
      {spec.auth === "oauth" && spec.hasWrite && (
        <label className="flex cursor-pointer items-start gap-3 rounded-xl border border-line p-3">
          <input type="checkbox" className="mt-1" checked={full} onChange={(e) => setFull(e.target.checked)} />
          <span><span className="block text-sm font-semibold">{spec.writeLabel}</span><span className="block text-xs text-muted">{spec.writeHint} Off by default; you can change it later in Manage.</span></span>
        </label>
      )}
      {error && <p role="alert" className="text-sm text-danger">{error}</p>}
      <button type="submit" disabled={busy || missing} className={`${buttonClass} px-5`}>{busy ? (spec.auth === "oauth" ? `Opening ${spec.name}…` : "Checking…") : spec.connectLabel}</button>
    </form>
  );
}

// ---- lists -------------------------------------------------------------------------------------

function ItemRows({ items, onDrill, empty }: { items: ProviderItem[]; onDrill?: (i: ProviderItem) => void; empty: string }) {
  if (items.length === 0) return <EmptyState>{empty}</EmptyState>;
  return (
    <Rows>
      {items.map((i) => (
        <li key={i.id} className="px-3 py-2">
          <div className="flex items-start gap-3">
            {i.image && /^https:\/\//.test(i.image) && (
              // eslint-disable-next-line @next/next/no-img-element -- thumbnails come from many provider hosts
              <img src={i.image} alt="" loading="lazy" referrerPolicy="no-referrer" className="h-10 w-10 shrink-0 rounded-lg bg-surface-2 object-cover" />
            )}
            <div className="min-w-0 flex-1">
              {i.url && /^https?:\/\//.test(i.url) ? <a href={i.url} target="_blank" rel="noreferrer" className="block truncate text-sm font-semibold hover:underline">{i.title}</a> : <span className="block truncate text-sm font-semibold">{i.title}</span>}
              {i.subtitle && <span className="block truncate text-xs text-muted">{i.subtitle}</span>}
            </div>
            {i.badge && <span className="shrink-0 rounded-full bg-surface-2 px-2 py-0.5 text-xs font-semibold">{i.badge}</span>}
            {i.drill && onDrill && <button type="button" onClick={() => onDrill(i)} className="shrink-0 text-xs font-semibold text-accent hover:underline">Open</button>}
          </div>
          {i.body && <pre className="mt-2 max-h-[40vh] overflow-auto whitespace-pre-wrap rounded-xl border border-line bg-surface-2 p-3 text-sm">{i.body}</pre>}
        </li>
      ))}
    </Rows>
  );
}

function DrillModal({ appKey, from, onClose }: { appKey: ProviderAppKey; from: ProviderItem | null; onClose: () => void }) {
  const [items, setItems] = useState<ProviderItem[] | null>(null);
  const [error, setError] = useState<Error | null>(null);
  const [tick, setTick] = useState(0);
  useEffect(() => {
    if (!from?.drill) return;
    let live = true;
    const t = setTimeout(() => { api.providerView(appKey, from.drill!, { arg: from.id }).then((r) => live && (setItems(r), setError(null)), (e) => live && setError(e instanceof Error ? e : new Error("Could not open that."))); }, 0);
    return () => { live = false; clearTimeout(t); };
  }, [appKey, from, tick]);
  return (
    <Modal wide open={from !== null} onClose={onClose}>
      {from && (
        <div>
          <h2 className="pr-10 text-xl font-bold">{from.title}</h2>
          <div className="mt-3">
            {error ? <ErrorState error={error} onRetry={() => { setItems(null); setError(null); setTick((n) => n + 1); }} /> : items === null ? <Loading label="Opening" /> : <ItemRows items={items} empty="Nothing here." />}
          </div>
        </div>
      )}
    </Modal>
  );
}

function ViewTab({ appKey, view }: { appKey: ProviderAppKey; view: ProviderView }) {
  const needsInput = view.inputs.some((i) => i.required);
  const [values, setValues] = useState<Record<string, string>>(() => defaults(view.inputs));
  const [applied, setApplied] = useState<Record<string, string> | null>(needsInput ? null : defaults(view.inputs));
  const [items, setItems] = useState<ProviderItem[] | null>(null);
  const [error, setError] = useState<Error | null>(null);
  const [tick, setTick] = useState(0);
  const [drill, setDrill] = useState<ProviderItem | null>(null);
  useEffect(() => {
    if (applied === null) return;
    let live = true;
    const t = setTimeout(() => { api.providerView(appKey, view.id, applied).then((r) => live && (setItems(r), setError(null)), (e) => live && setError(e instanceof Error ? e : new Error("Could not load this list."))); }, 0);
    return () => { live = false; clearTimeout(t); };
  }, [appKey, view.id, applied, tick]);

  function submit(e: FormEvent) { e.preventDefault(); setItems(null); setError(null); setApplied({ ...values }); setTick((n) => n + 1); }

  return (
    <section className={`space-y-3 p-4 ${tileClass}`}>
      {view.inputs.length > 0 && (
        <form onSubmit={submit} className="grid gap-3 sm:grid-cols-2">
          {view.inputs.map((i) => <InputField key={i.key} appKey={appKey} input={i} value={values[i.key] ?? ""} onChange={(v) => setValues((c) => ({ ...c, [i.key]: v }))} idPrefix={`view-${view.id}`} />)}
          <div className="flex items-end"><button type="submit" className={`${buttonClass} px-5`}>{needsInput ? "Show" : "Update"}</button></div>
        </form>
      )}
      {applied === null ? <p className="text-sm text-muted">Fill in the details above to see results.</p>
        : error ? <ErrorState error={error} onRetry={() => { setItems(null); setError(null); setTick((n) => n + 1); }} />
        : items === null ? <Loading label={`Loading ${view.label.toLowerCase()}`} />
        : <ItemRows items={items} onDrill={setDrill} empty={view.empty ?? "Nothing to show."} />}
      <DrillModal appKey={appKey} from={drill} onClose={() => setDrill(null)} />
    </section>
  );
}

// ---- actions -----------------------------------------------------------------------------------

function ActionCard({ appKey, action, canWrite }: { appKey: ProviderAppKey; action: ProviderAction; canWrite: boolean }) {
  const [values, setValues] = useState<Record<string, string>>(() => defaults(action.inputs));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<{ message: string; url: string | null } | null>(null);
  const missing = action.inputs.some((i) => i.required && !values[i.key]?.trim());

  async function run(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null); setDone(null);
    try { setDone(await api.providerAction(appKey, action.id, values)); }
    catch (err) { setError(errorText(err, "That did not work.")); }
    finally { setBusy(false); }
  }

  return (
    <form onSubmit={(e) => void run(e)} className={`space-y-3 p-4 ${tileClass}`}>
      <h3 className="text-base font-bold">{action.label}</h3>
      {action.hint && <p className="text-xs text-muted">{action.hint}</p>}
      {action.inputs.map((i) => <InputField key={i.key} appKey={appKey} input={i} value={values[i.key] ?? ""} onChange={(v) => setValues((c) => ({ ...c, [i.key]: v }))} idPrefix={`action-${action.id}`} />)}
      {error && <p role="alert" className="text-sm text-danger">{error}</p>}
      {done && <p role="status" className="text-sm text-muted">{done.message} {done.url && /^https?:\/\//.test(done.url) && <a href={done.url} target="_blank" rel="noreferrer" className="font-semibold underline">Open</a>}</p>}
      <button type="submit" disabled={busy || missing || !canWrite} className={buttonClass}>{busy ? "Working…" : action.label}</button>
      {!canWrite && <p className="text-xs text-muted">Daksha has read-only access. Turn on full access in Manage to do this.</p>}
    </form>
  );
}

// ---- the page, the Manage popup and the dashboard tile ------------------------------------------

function Body({ spec, status }: { spec: ProviderSpec; status: OneAppStatus }) {
  const views = spec.views.filter((v) => !v.hidden);
  const tabs = [...views.map((v) => ({ id: v.id, label: v.label })), ...(spec.actions.length > 0 ? [{ id: "__actions", label: "Do something" }] : [])];
  const [tab, setTab] = useState(tabs[0]?.id ?? "");
  const active = views.find((v) => v.id === tab);
  return (
    <>
      <div role="tablist" aria-label={spec.name} className="flex flex-wrap gap-1">
        {tabs.map((t) => <button key={t.id} role="tab" aria-selected={tab === t.id} onClick={() => setTab(t.id)} className={`rounded-full px-4 py-2 text-sm font-semibold ${tab === t.id ? "bg-accent text-accent-contrast" : "border border-line text-muted hover:bg-surface-2"}`}>{t.label}</button>)}
      </div>
      {active && <ViewTab key={`${spec.key}:${active.id}`} appKey={spec.key} view={active} />}
      {tab === "__actions" && <div className="grid gap-4 lg:grid-cols-2">{spec.actions.map((a) => <ActionCard key={a.id} appKey={spec.key} action={a} canWrite={status.write} />)}</div>}
    </>
  );
}

function manageRenderer(config: OneAppConfig) {
  return function renderManage(onChanged: () => void): ReactNode { return <OneAppManagePanel config={config} onChanged={onChanged} />; };
}

function WorkspaceInner({ spec }: { spec: ProviderSpec }) {
  const config = useMemo(() => configFromSpec(spec), [spec]);
  return <OneAppShell config={config} manage={manageRenderer(config)}>{(status) => <Body spec={spec} status={status} />}</OneAppShell>;
}

/** The /apps/{key} page of any provider-engine app. */
export function ProviderAppWorkspace({ appKey }: { appKey: ProviderAppKey }) {
  const { state, reload } = useApi(() => api.providerSpec(appKey));
  if (state.status === "loading") return <Loading label="Loading" />;
  if (state.status === "error") return <ErrorState error={state.error} onRetry={reload} />;
  return <><PageHeader title={state.data.name}>{state.data.account}</PageHeader><WorkspaceInner spec={state.data} /></>;
}

/** The Manage popup of any provider-engine app (used from the Apps page). */
export function ProviderManagePanel({ appKey, onChanged }: { appKey: ProviderAppKey; onChanged?: () => void }) {
  const { state, reload } = useApi(() => api.providerSpec(appKey));
  const config = useMemo(() => (state.status === "ready" ? configFromSpec(state.data) : null), [state]);
  if (state.status === "loading" || !config) return state.status === "error" ? <ErrorState error={state.error} onRetry={reload} /> : <Loading label="Loading" />;
  return <OneAppManagePanel config={config} onChanged={onChanged} />;
}

/** The dashboard tile of any provider-engine app. */
export function ProviderDashboardCard({ app, onChanged }: { app: DashboardApp; onChanged?: () => void }) {
  const key = app.integrationKey as ProviderAppKey;
  const { state, reload } = useApi(() => api.providerSpec(key));
  const config = useMemo(() => (state.status === "ready" ? configFromSpec(state.data) : null), [state]);
  if (state.status === "error") return <div className={`p-4 ${tileClass}`}><ErrorState error={state.error} onRetry={reload} /></div>;
  if (!config) return <div className={`p-4 ${tileClass}`}><Loading label={`Loading ${app.name}`} /></div>;
  return <OneAppDashboardCard app={app} config={config} manage={manageRenderer(config)} onChanged={onChanged} />;
}

